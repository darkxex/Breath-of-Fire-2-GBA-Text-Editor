using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Encodings.Web;

internal static class Program
{
	private const uint ScriptStart = 0x13EBB0;
	private const uint PointerTableStart = 0x1895B4;
	private const uint PointerTableStop = 0x18D5B4;
	private const uint SpecialStopAtFirstTerminatorPointer = 0x18D5B0;
	private const int PointerEntrySize = 4;

	private const string DefaultRomFile = "Breath of Fire II (Europe).gba";
	private const string DefaultEditableScript = "script_bof2.json";
	private const string DefaultPatchedRom = "Breath of Fire II (Europe)_patched.gba";
	private const string DefaultTableFile = "bof2.gba.tbl";
	// private const string DefaultRebuiltScript = "BOF2_script_recalculated.txt";
	private const string DefaultMetadataExtension = ".meta.json";

	private static Dictionary<string, byte[]> TokenToBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
	private static Dictionary<char, byte[]> LiteralMap = new Dictionary<char, byte[]>();
	private static List<TokenDefinition> DecodeTokens = new List<TokenDefinition>();

	public static int Main(string[] args)
	{
		try
		{
			if (args.Length == 0 || IsCommand(args[0], "extract"))
			{
				RunExtract(args);
				return 0;
			}

			if (IsCommand(args[0], "insert") || IsCommand(args[0], "patch"))
			{
				RunInsert(args);
				return 0;
			}

			ShowUsage();
			return 1;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	private static void RunExtract(string[] args)
	{
		string inputPath = args.Length > 1 ? args[1] : DefaultRomFile;
		string outputPath = args.Length > 2 ? args[2] : DefaultEditableScript;
		string tablePath = args.Length > 3 ? args[3] : DefaultTableFile;

		ConfigureTokenTables(tablePath);

		if (!File.Exists(inputPath))
		{
			throw new FileNotFoundException($"No se encontró el ROM: {inputPath}", inputPath);
		}

		byte[] rom = File.ReadAllBytes(inputPath);
		List<ScriptEntry> entries = ParseRomScript(rom);
		WriteEditableScript(outputPath, entries);

		Console.WriteLine($"Extraído: {outputPath} ({entries.Count} bloques)");
	}

	private static void RunInsert(string[] args)
	{
		string romPath = args.Length > 1 ? args[1] : DefaultRomFile;
		string scriptPath = args.Length > 2 ? args[2] : DefaultEditableScript;
		string outputRomPath = args.Length > 3 ? args[3] : DefaultPatchedRom;
		string tablePath = args.Length > 4 ? args[4] : DefaultTableFile;

		ConfigureTokenTables(tablePath);

		if (!File.Exists(romPath))
		{
			throw new FileNotFoundException($"No se encontró el ROM: {romPath}", romPath);
		}

		ExtractionMetadata metadata = LoadExtractionMetadata(scriptPath);
		List<ScriptEntry> entries = ParseEditableScript(scriptPath);
		byte[] rom = File.ReadAllBytes(romPath);
		byte[] rebuiltRom = BuildPatchedRom(rom, entries, metadata);

		File.WriteAllBytes(outputRomPath, rebuiltRom);

		Console.WriteLine($"Insertado: {outputRomPath}");
	}

	private static void ShowUsage()
	{
		Console.WriteLine("Uso:");
		Console.WriteLine("  BoF2Manager extract [ROM.gba] [script_bof2.json] [tabla.tbl]");
		Console.WriteLine("  BoF2Manager insert [ROM.gba] [script_bof2.json] [ROM_patched.gba] [tabla.tbl]");
	}

	private static void ConfigureTokenTables(string tablePath)
	{
		TokenToBytes = LoadTokenTable(tablePath);
		LiteralMap = LoadLiteralMap(TokenToBytes);
		DecodeTokens = LoadDecodeTokens(TokenToBytes);
	}

	private static bool IsCommand(string value, string expected)
	{
		return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
	}

	private static List<ScriptEntry> ParseRomScript(byte[] rom)
	{
		int totalTableEntries = checked((int)((PointerTableStop - PointerTableStart) / PointerEntrySize));
		List<ScriptEntry> entries = new List<ScriptEntry>(totalTableEntries);

		for (int i = 0; i < totalTableEntries; i++)
		{
			uint pointerAddress = PointerTableStart + (uint)(i * PointerEntrySize);
			uint stringAddress = ReadPointerValue(rom, pointerAddress);
			ScriptEntry entry = new ScriptEntry(pointerAddress)
			{
				StringAddress = stringAddress == 0 ? 0 : stringAddress - 0x08000000u
			};

			if (entry.StringAddress != 0)
			{
				entry.ByteLength = 0;
			}

			entries.Add(entry);
		}

		List<uint> sortedStringAddresses = entries
			.Where(e => e.StringAddress != 0)
			.Select(e => e.StringAddress)
			.Distinct()
			.OrderBy(v => v)
			.ToList();

		foreach (ScriptEntry entry in entries)
		{
			if (entry.StringAddress == 0)
			{
				continue;
			}

			int startPos = checked((int)entry.StringAddress);
			if (startPos > 0 && rom[startPos - 1] != 0x00)
			{
				continue;
			}

			bool stopAtFirstTerminator = entry.PointerAddress == SpecialStopAtFirstTerminatorPointer;
			int finalMaxOffset = stopAtFirstTerminator
				? rom.Length
				: GetDialogLogicalEnd(rom, entry.StringAddress, sortedStringAddresses);
			int totalByteLength = GetRawDialogByteLength(rom, entry.StringAddress, finalMaxOffset, stopAtFirstTerminator);
			entry.ByteLength = totalByteLength;
			entry.Lines.Clear();
			entry.Lines.Add(DecodeRomText(rom, entry.StringAddress, finalMaxOffset, stopAtFirstTerminator));
		}

		RepairEmptySplitDialogEntries(rom, entries, sortedStringAddresses);

		return entries;
	}

	private static void RepairEmptySplitDialogEntries(byte[] rom, List<ScriptEntry> entries, List<uint> sortedStringAddresses)
	{
		// Only recover text for entries that were extracted as empty (no lines)
		// Do NOT modify neighbouring entries or change the logical end detection.
		for (int i = 0; i < entries.Count; i++)
		{
			ScriptEntry entry = entries[i];
			if (entry.StringAddress == 0)
				continue;

			if (entry.Lines.Count > 0)
				continue; // only act on truly empty extractions

			// Find next sorted start to determine the available bytes for this string
			int nextStart = GetStringDecodeLimit(entry.StringAddress, sortedStringAddresses, rom.Length);

			int endOffset = Math.Min(nextStart, rom.Length);
			if (endOffset <= 0 || endOffset <= entry.StringAddress)
			{
				continue;
			}

		entry.Lines.Clear();
			entry.Lines.Add(DecodeRomText(rom, entry.StringAddress, endOffset, entry.PointerAddress == SpecialStopAtFirstTerminatorPointer));
			entry.ByteLength = GetRawDialogByteLength(rom, entry.StringAddress, endOffset, entry.PointerAddress == SpecialStopAtFirstTerminatorPointer);
			entry.IsRecovered = true; // Marcar como recuperado
		}
	}

	private static int GetDialogLogicalEnd(byte[] rom, uint dialogStart, List<uint> sortedStringAddresses)
	{
		int index = sortedStringAddresses.IndexOf(dialogStart);
		if (index < 0)
		{
			return rom.Length;
		}

		for (int i = index + 1; i < sortedStringAddresses.Count; i++)
		{
			int nextStart = checked((int)sortedStringAddresses[i]);
			if (nextStart <= 0 || nextStart > rom.Length)
			{
				return rom.Length;
			}

			if (rom[nextStart - 1] == 0x00)
			{
				return nextStart;
			}
		}

		return rom.Length;
	}

	private static int GetStringDecodeLimit(uint stringAddress, List<uint> sortedStringAddresses, int romLength)
	{
		for (int i = 0; i < sortedStringAddresses.Count; i++)
		{
			if (sortedStringAddresses[i] <= stringAddress)
			{
				continue;
			}

			return checked((int)sortedStringAddresses[i]);
		}

		return romLength;
	}

	private static List<ScriptEntry> ParseEditableScript(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException($"No se encontró el script editable: {path}", path);
		}

		if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
		{
			string json = File.ReadAllText(path, Encoding.UTF8);
			try
			{
				var items = JsonSerializer.Deserialize<List<JsonScriptItem>>(json);
				if (items == null)
				{
					throw new InvalidDataException($"El JSON no contiene elementos válidos: {path}");
				}

				List<ScriptEntry> entries = new List<ScriptEntry>(items.Count);
				foreach (var it in items)
				{
					uint addr = uint.Parse(it.pointerAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
					ScriptEntry e = new ScriptEntry(addr);
					if (!string.IsNullOrEmpty(it.dialog))
					{
						e.Lines.Add(it.dialog);
					}
					// Preservar la dirección original si está en el JSON
					if (!string.IsNullOrEmpty(it.stringAddress))
					{
						e.StringAddress = uint.Parse(it.stringAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
					}
					entries.Add(e);
				}

				return entries;
			}
			catch (JsonException ex)
			{
				throw new InvalidDataException($"No se pudo parsear el JSON del script: {ex.Message}");
			}
		}
		else
		{
			List<ScriptEntry> entries = new List<ScriptEntry>();
			ScriptEntry current = null;

			foreach (string rawLine in File.ReadLines(path))
			{
				string line = rawLine.TrimEnd('\r');

				if (line.StartsWith("[ADDR:", StringComparison.OrdinalIgnoreCase))
				{
					if (current != null)
					{
						entries.Add(current);
					}

					current = new ScriptEntry(ParseEditableAddress(line));

					string firstContent = ExtractEditableContent(line);
					if (firstContent.Length > 0)
					{
						current.Lines.Add(firstContent);
					}

					continue;
				}

				if (current == null)
				{
					continue;
				}

				string content = line.Trim();
				if (content.Length > 0)
				{
					current.Lines.Add(content);
				}
			}

			if (current != null)
			{
				entries.Add(current);
			}

			if (entries.Count == 0)
			{
				throw new InvalidDataException($"No se encontraron bloques editables válidos en {path}");
			}

			return entries;
		}
	}

	private static void WriteEditableScript(string path, IReadOnlyList<ScriptEntry> entries)
	{
		EnsureDirectoryForFile(path);

		if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
		{
			var items = entries.Select(e => 
			{
				byte[] originalPayload = EncodeLogicalText(e.Text);
				return new JsonScriptItem
				{
					dialog = e.Text,
					pointerAddress = e.PointerAddress.ToString("X6", CultureInfo.InvariantCulture),
					stringAddress = e.StringAddress.ToString("X6", CultureInfo.InvariantCulture),
					originalByteLength = originalPayload.Length,
					recovered = e.IsRecovered
				};
			}).ToList();
			JsonSerializerOptions options = new JsonSerializerOptions 
			{ 
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};
			File.WriteAllText(path, JsonSerializer.Serialize(items, options), new UTF8Encoding(false));
			return;
		}

		StringBuilder builder = new StringBuilder();
		foreach (ScriptEntry entry in entries)
		{
			builder.Append("[ADDR:");
			builder.Append(entry.PointerAddress.ToString("X6", CultureInfo.InvariantCulture));
			builder.Append("]|");

			if (entry.Lines.Count > 0)
			{
				builder.AppendLine(entry.Lines[0]);

				for (int i = 1; i < entry.Lines.Count; i++)
				{
					builder.AppendLine(entry.Lines[i]);
				}
			}
			else
			{
				builder.AppendLine();
			}
		}

		File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
	}

	private static void WriteRebuiltScript(string path, IReadOnlyList<ScriptEntry> entries)
	{
		EnsureDirectoryForFile(path);

		StringBuilder builder = new StringBuilder();
		foreach (ScriptEntry entry in entries)
		{
			builder.Append("[ADDR:");
			builder.Append(entry.PointerAddress.ToString("X6", CultureInfo.InvariantCulture));
			builder.Append("]|");
			builder.AppendLine(entry.Text);
		}

		File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
	}

	private static void WriteExtractionMetadata(string path, IReadOnlyList<ScriptEntry> entries)
	{
		EnsureDirectoryForFile(path);

		ExtractionMetadata metadata = new ExtractionMetadata();
		foreach (ScriptEntry entry in entries)
		{
			byte[] originalPayload = EncodeLogicalText(entry.Text);
			metadata.Entries.Add(new ExtractionEntry
			{
				PointerAddress = entry.PointerAddress,
				StringAddress = entry.StringAddress,
				OriginalByteLength = originalPayload.Length,
				Recovered = entry.IsRecovered
			});
		}

		JsonSerializerOptions options = new JsonSerializerOptions 
		{ 
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
		File.WriteAllText(path, JsonSerializer.Serialize(metadata, options), new UTF8Encoding(false));
	}

	private static ExtractionMetadata LoadExtractionMetadata(string scriptPath)
	{
		if (!File.Exists(scriptPath))
		{
			throw new FileNotFoundException($"No se encontró el archivo de script: {scriptPath}", scriptPath);
		}

		if (!string.Equals(Path.GetExtension(scriptPath), ".json", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Solo se soportan archivos JSON para cargar metadatos: {scriptPath}");
		}

		string json = File.ReadAllText(scriptPath, Encoding.UTF8);
		var items = JsonSerializer.Deserialize<List<JsonScriptItem>>(json);
		if (items == null)
		{
			throw new InvalidDataException($"No se pudo leer el archivo de script: {scriptPath}");
		}

		ExtractionMetadata metadata = new ExtractionMetadata();
		foreach (var item in items)
		{
			int originalByteLength = item.originalByteLength;
			
			uint pointerAddress = string.IsNullOrEmpty(item.pointerAddress) ? 0 : uint.Parse(item.pointerAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			uint stringAddress = string.IsNullOrEmpty(item.stringAddress) ? 0 : uint.Parse(item.stringAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

			metadata.Entries.Add(new ExtractionEntry
			{
				PointerAddress = pointerAddress,
				StringAddress = stringAddress,
				OriginalByteLength = originalByteLength,
				Recovered = item.recovered
			});
		}

		return metadata;
	}

	private static byte[] BuildPatchedRom(byte[] rom, IReadOnlyList<ScriptEntry> entries, ExtractionMetadata metadata)
	{
		int totalTableEntries = checked((int)((PointerTableStop - PointerTableStart) / PointerEntrySize));
		if (entries.Count != totalTableEntries)
		{
			throw new InvalidDataException($"Se esperaban {totalTableEntries} entradas en el script editable. Usa el bloque completo.");
		}

		byte[] output = (byte[])rom.Clone();
		var packedTextAddresses = new Dictionary<string, uint>(StringComparer.Ordinal);
		var freeGaps = new List<(int Offset, int Size)>();
		var pendingEntries = new List<ScriptEntry>();

		// Fase 1: Intenta ajustar cada diálogo en su ubicación original en ROM
		InsertPhase1_OriginalLocations(entries, metadata, ref output, packedTextAddresses, freeGaps, pendingEntries);

		// Fase 2: Recicla espacios vacíos de diálogos anteriores que fueron comprimidos
		InsertPhase2_RecycleGaps(pendingEntries, ref output, packedTextAddresses, freeGaps);

		// Fase 3: Escribe el resto de diálogos al final de la ROM (con extensión de ROM si es necesario)
		int extensionCursor = -1;
		InsertPhase3_RomExtension(pendingEntries, ref output, packedTextAddresses, ref extensionCursor);

		// Fase 4: Actualiza la tabla de punteros con las nuevas direcciones
		InsertPhase4_UpdatePointerTable(entries, output);

		return output;
	}

	private static void InsertPhase1_OriginalLocations(IReadOnlyList<ScriptEntry> entries, ExtractionMetadata metadata, ref byte[] output,
		Dictionary<string, uint> packedTextAddresses, List<(int Offset, int Size)> freeGaps, List<ScriptEntry> pendingEntries)
	{
		// Intenta escribir cada diálogo en su ubicación original si el nuevo texto cabe
		for (int i = 0; i < entries.Count; i++)
		{
			ScriptEntry entry = entries[i];
			var meta = metadata.Entries.FirstOrDefault(m => m.PointerAddress == entry.PointerAddress);

			if (string.IsNullOrEmpty(entry.Text))
			{
				entry.StringAddress = 0;
				continue;
			}

			// Si el texto es idéntico a uno anterior, reutilizar su dirección
			if (packedTextAddresses.TryGetValue(entry.Text, out uint sharedAddress))
			{
				entry.StringAddress = sharedAddress;
				continue;
			}

			// Los diálogos recuperados van directamente a Fase 3 para evitar solapamientos con el diálogo padre
			if (meta != null && meta.Recovered)
			{
				pendingEntries.Add(entry);
				continue;
			}

			byte[] payload = EncodeLogicalText(entry.Text);
			bool written = false;

			// Si el diálogo original tenía una ubicación conocida
			if (meta != null && meta.StringAddress != 0)
			{
				// Si el nuevo texto cabe en el espacio original, escribirlo ahí
				if (payload.Length <= meta.OriginalByteLength)
				{
					Buffer.BlockCopy(payload, 0, output, (int)meta.StringAddress, payload.Length);
					// Limpiar el resto del espacio original con bytes 0x00
					for (int k = payload.Length; k < meta.OriginalByteLength; k++)
						output[(int)meta.StringAddress + k] = 0x00;

					entry.StringAddress = meta.StringAddress;
					packedTextAddresses[entry.Text] = entry.StringAddress;
					written = true;
				}
				else
				{
					// Si no cabe, registrar el hueco para reutilizarlo más adelante (Fase 2)
					freeGaps.Add(((int)meta.StringAddress, meta.OriginalByteLength));
				}
			}

			if (!written)
			{
				pendingEntries.Add(entry);
			}
		}
	}

	private static void InsertPhase2_RecycleGaps(List<ScriptEntry> pendingEntries, ref byte[] output,
		Dictionary<string, uint> packedTextAddresses, List<(int Offset, int Size)> freeGaps)
	{
		// Intenta ajustar los diálogos que no cupieron (Phase 1) en los huecos vacíos
		var remainingEntries = new List<ScriptEntry>();

		foreach (var entry in pendingEntries)
		{
			// Si ya está en el diccionario de direcciones (fue plegado), continuar
			if (packedTextAddresses.TryGetValue(entry.Text, out uint sharedAddress))
			{
				entry.StringAddress = sharedAddress;
				continue;
			}

			byte[] payload = EncodeLogicalText(entry.Text);
			bool placed = false;

			// Buscar un hueco suficientemente grande para este diálogo
			for (int g = 0; g < freeGaps.Count; g++)
			{
				if (payload.Length <= freeGaps[g].Size)
				{
					int recycledOffset = freeGaps[g].Offset;
					Buffer.BlockCopy(payload, 0, output, recycledOffset, payload.Length);
					entry.StringAddress = (uint)recycledOffset;
					packedTextAddresses[entry.Text] = entry.StringAddress;
					freeGaps.RemoveAt(g);
					placed = true;
					break;
				}
			}

			if (!placed)
			{
				remainingEntries.Add(entry);
			}
		}

		// Los que no pudieron colocarse aquí irán a Fase 3
		pendingEntries.Clear();
		pendingEntries.AddRange(remainingEntries);
	}

	private static void InsertPhase3_RomExtension(List<ScriptEntry> pendingEntries, ref byte[] output,
		Dictionary<string, uint> packedTextAddresses, ref int extensionCursor)
	{
		// Los diálogos que no cupieron en ubicaciones anteriores se escriben al final (extensión de ROM)
		foreach (var entry in pendingEntries)
		{
			if (packedTextAddresses.TryGetValue(entry.Text, out uint sharedAddress))
			{
				entry.StringAddress = sharedAddress;
				continue;
			}

			byte[] payload = EncodeLogicalText(entry.Text);

			// Inicializar el cursor de extensión si es la primera escritura aquí
			if (extensionCursor < 0)
			{
				extensionCursor = (output.Length + 3) & ~3; // Alineación de 4 bytes
			}

			int writeOffset = extensionCursor;
			extensionCursor += payload.Length;

			EnsureCapacity(ref output, extensionCursor);
			Buffer.BlockCopy(payload, 0, output, writeOffset, payload.Length);

			entry.StringAddress = (uint)writeOffset;
			packedTextAddresses[entry.Text] = entry.StringAddress;
		}
	}

	private static void InsertPhase4_UpdatePointerTable(IReadOnlyList<ScriptEntry> entries, byte[] output)
	{
		// Actualiza la tabla de punteros (en 0x1895B4) con las nuevas direcciones de texto
		for (int i = 0; i < entries.Count; i++)
		{
			uint stringAddress = entries[i].StringAddress;
			if (stringAddress != 0)
			{
				uint pointerValue = 0x08000000u + stringAddress; // Dirección física (suma el offset de BIOS)
				int pointerTableOffset = checked((int)entries[i].PointerAddress);

				// Escribir 3 bytes de dirección (el 4to byte es flag del juego)
				output[pointerTableOffset + 0] = (byte)(pointerValue & 0xFF);
				output[pointerTableOffset + 1] = (byte)((pointerValue >> 8) & 0xFF);
				output[pointerTableOffset + 2] = (byte)((pointerValue >> 16) & 0xFF);
			}
		}
	}

	private static int Align4(int value)
	{
		return (value + 3) & ~3;
	}

	private static void EnsureCapacity(ref byte[] buffer, int minLength)
	{
		if (minLength <= buffer.Length)
		{
			return;
		}

		// Límite máximo físico de una ROM de GBA (32MB)
		if (minLength > 32 * 1024 * 1024)
		{
			throw new InvalidOperationException($"Error crítico: La ROM resultante ({minLength} bytes) excede el límite de 32MB de GBA.");
		}

		int newLength = minLength;
		Array.Resize(ref buffer, newLength);
	}

	private static uint ReadPointerValue(byte[] rom, uint address)
	{
		int offset = checked((int)address);
		if (offset < 0 || offset + 3 >= rom.Length)
		{
			throw new InvalidDataException($"La tabla de punteros sale del ROM en ${address:X6}.");
		}

		return (uint)(rom[offset]
			| (rom[offset + 1] << 8)
			| (rom[offset + 2] << 16)
			| (rom[offset + 3] << 24));
	}

	private static string DecodeRomText(byte[] rom, uint stringAddress, int maxOffsetExclusive, bool stopAtB0)
	{
		int offset = checked((int)stringAddress);
		StringBuilder builder = new StringBuilder();
		int maxOffset = Math.Min(maxOffsetExclusive, rom.Length);

		while (offset < maxOffset)
		{
			if (stopAtB0 && rom[offset] == 0xB0)
			{
				break;
			}

			bool matched = false;
			foreach (TokenDefinition token in DecodeTokens)
			{
				if (token.Bytes.Length == 0 || offset + token.Bytes.Length > maxOffset)
				{
					continue;
				}

				if (MatchesAt(rom, offset, token.Bytes))
				{
					builder.Append(FormatDecodedToken(token));
					offset += token.Bytes.Length;
					matched = true;
					break;
				}
			}

			if (!matched)
			{
				if (rom[offset] == 0x00)
				{
					builder.Append("<$00>");
					offset++;
					continue;
				}

				builder.Append("[$");
				builder.Append(rom[offset].ToString("X2", CultureInfo.InvariantCulture));
				builder.Append("]");
				offset++;
			}
		}

		return builder.ToString();
	}

	private static int GetRawDialogByteLength(byte[] rom, uint stringAddress, int maxOffsetExclusive, bool stopAtB0)
	{
		int start = checked((int)stringAddress);
		if (start < 0 || start >= rom.Length)
		{
			return 0;
		}

		int maxOffset = Math.Min(maxOffsetExclusive, rom.Length);
		if (!stopAtB0)
		{
			return Math.Max(0, maxOffset - start);
		}

		int offset = start;
		while (offset < maxOffset && rom[offset] != 0xB0)
		{
			offset++;
		}

		return offset - start;
	}

	private static string FormatDecodedToken(TokenDefinition token)
	{
		return token.Token;
	}

	private static bool MatchesAt(byte[] rom, int offset, byte[] bytes)
	{
		for (int i = 0; i < bytes.Length; i++)
		{
			if (rom[offset + i] != bytes[i])
			{
				return false;
			}
		}

		return true;
	}

	private static byte[] EncodeLogicalText(string text)
	{
		List<byte> bytes = new List<byte>();

		for (int i = 0; i < text.Length; i++)
		{
			char current = text[i];

			if (current == '\r' || current == '\n')
			{
				continue;
			}

			if (current == '\\' && i + 1 < text.Length)
			{
				char escaped = text[i + 1];
				if (escaped == 'n' || escaped == 'r')
				{
					i++;
					continue;
				}
			}

			if (current == '[' || current == '<')
			{
				char close = current == '[' ? ']' : '>';
				int end = text.IndexOf(close, i + 1);
				if (end > i)
				{
					string fullToken = text.Substring(i, end - i + 1);
					if (TryEncodeToken(fullToken, bytes))
					{
						i = end;
						continue;
					}
				}
			}


			if (!TryEncodeLiteral(current, bytes))
			{
				throw new InvalidDataException($"No se puede codificar el carácter '{current}' en el texto editado.");
			}
		}

		if (!EndsWithTerminator(bytes))
		{
			bytes.Add(0x00);
		}

		return bytes.ToArray();
	}

	private static bool TryEncodeToken(string token, List<byte> output)
	{
		if (token.Length == 0)
		{
			return false;
		}

		if (string.Equals(token, "end_ptr", StringComparison.OrdinalIgnoreCase))
		{
			output.Add(0x00);
			return true;
		}

		// Soporte para comandos con parámetros: [COLOR:05] -> Busca "[COLOR]" en tabla y añade "05" como byte literal
		if (token.Contains(':'))
		{
			int colonIndex = token.IndexOf(':');
			string prefix = token.Substring(0, 1); // '[' o '<'
			string suffix = token.Substring(token.Length - 1, 1); // ']' o '>'
			string cmdName = token.Substring(1, colonIndex - 1);
			string valPart = token.Substring(colonIndex + 1, token.Length - colonIndex - 2);

			string lookupToken = prefix + cmdName + suffix;

			if (TokenToBytes.TryGetValue(lookupToken, out byte[] cmdBytes))
			{
				output.AddRange(cmdBytes);
				// Convertimos el parámetro hex (ej: "01" o "0102") a bytes
				output.AddRange(HexToBytes(valPart));
				return true;
			}
		}

		if (TryGetTokenBytes(token, out byte[] bytes))
		{
			output.AddRange(bytes);
			return true;
		}

		string inner = token;
		if (inner.Length >= 2 && (inner[0] == '[' || inner[0] == '<'))
			inner = inner.Substring(1, inner.Length - 2); // quita el bracket de apertura y cierre

		string hex = inner.StartsWith("$", StringComparison.Ordinal) ? inner.Substring(1) : inner;
		if (hex.Length > 0 && hex.Length % 2 == 0 && hex.All(IsHexDigit))
		{
			for (int i = 0; i < hex.Length; i += 2)
				output.Add(byte.Parse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
			return true;
		}
		return false;
	}

	private static bool TryGetTokenBytes(string token, out byte[] bytes)
	{
		if (TokenToBytes.TryGetValue(token, out bytes))
		{
			return true;
		}

		string[] variants = new[]
		{
			token + "\\n",
			token + "\\n\\n",
			token + "\\r",
			token + "\\r\\n",
			token + "\\r\\n\\r\\n"
		};

		foreach (string variant in variants)
		{
			if (TokenToBytes.TryGetValue(variant, out bytes))
			{
				return true;
			}
		}

		bytes = null;
		return false;
	}

	private static byte[] HexToBytes(string hex)
	{
		if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
		
		// Limpieza básica por si hay espacios
		hex = hex.Replace(" ", "");

		if (hex.Length % 2 != 0 || !hex.All(IsHexDigit))
			throw new InvalidDataException($"El parámetro hexadecimal '[...:{hex}]' no es válido. Debe ser un número par de caracteres (0-9, A-F).");

		byte[] bytes = new byte[hex.Length / 2];
		for (int i = 0; i < bytes.Length; i++)
		{
			bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		}
		return bytes;
	}

	private static bool TryEncodeLiteral(char value, List<byte> output)
	{
		if (LiteralMap.TryGetValue(value, out byte[] bytes))
		{
			output.AddRange(bytes);
			return true;
		}

		if (value <= 0x7F)
		{
			output.Add((byte)value);
			return true;
		}

		return false;
	}

	private static bool EndsWithTerminator(List<byte> bytes)
	{
		return bytes.Count > 0 && bytes[bytes.Count - 1] == 0x00;
	}


	private static uint ParseEditableAddress(string line)
	{
		Match match = Regex.Match(line, @"^\[ADDR:([0-9A-Fa-f]+)\](?:\||$)");
		if (!match.Success)
		{
			throw new InvalidDataException($"No se pudo leer la dirección del script: {line}");
		}

		return uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	}

	private static string ExtractEditableContent(string line)
	{
		int separator = line.IndexOf('|');
		if (separator < 0 || separator == line.Length - 1)
		{
			return string.Empty;
		}

		return line.Substring(separator + 1).Trim();
	}

	private static void EnsureDirectoryForFile(string path)
	{
		string directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
	}

	private static bool IsHexDigit(char value)
	{
		return (value >= '0' && value <= '9') ||
			   (value >= 'a' && value <= 'f') ||
			   (value >= 'A' && value <= 'F');
	}

	private static Dictionary<string, byte[]> LoadTokenTable(string tablePath)
	{
		string resolvedPath = tablePath;
		if (string.IsNullOrWhiteSpace(resolvedPath))
		{
			resolvedPath = DefaultTableFile;
		}

		if (!Path.IsPathRooted(resolvedPath))
		{
			string fromCurrentDirectory = Path.Combine(Directory.GetCurrentDirectory(), resolvedPath);
			if (File.Exists(fromCurrentDirectory))
			{
				resolvedPath = fromCurrentDirectory;
			}
			else
			{
				string fromExecutableDirectory = Path.Combine(AppContext.BaseDirectory, resolvedPath);
				if (File.Exists(fromExecutableDirectory))
				{
					resolvedPath = fromExecutableDirectory;
				}
			}
		}

		if (!File.Exists(resolvedPath))
		{
			throw new FileNotFoundException($"No se encontró la tabla: {tablePath}");
		}

		Dictionary<string, byte[]> map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		foreach (string rawLine in File.ReadLines(resolvedPath, Encoding.UTF8))
		{
			if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith("#", StringComparison.Ordinal))
			{
				continue;
			}

			int equalsIndex = rawLine.IndexOf('=');
			if (equalsIndex <= 0)
			{
				continue;
			}

			string hexPart = rawLine.Substring(0, equalsIndex).Trim();
			string token = rawLine.Substring(equalsIndex + 1);

			if (hexPart.StartsWith("/", StringComparison.Ordinal) || hexPart.StartsWith("$", StringComparison.Ordinal))
			{
				hexPart = hexPart.Substring(1);
			}

			if (hexPart.Length == 0 || hexPart.Length % 2 != 0 || !hexPart.All(IsHexDigit))
			{
				continue;
			}

			byte[] bytes = new byte[hexPart.Length / 2];
			for (int i = 0; i < bytes.Length; i++)
			{
				bytes[i] = byte.Parse(hexPart.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			}

			if (!map.ContainsKey(token))
			{
				map[token] = bytes;
			}
		}

		return map;
	}

	private static Dictionary<char, byte[]> LoadLiteralMap(Dictionary<string, byte[]> tokenToBytes)
	{
		Dictionary<char, byte[]> map = new Dictionary<char, byte[]>();
		foreach (KeyValuePair<string, byte[]> pair in tokenToBytes)
		{
			if (pair.Key.Length == 1)
			{
				map[pair.Key[0]] = pair.Value;
			}
		}

		return map;
	}

	private static List<TokenDefinition> LoadDecodeTokens(Dictionary<string, byte[]> tokenToBytes)
	{
		List<TokenDefinition> tokens = new List<TokenDefinition>();
		foreach (KeyValuePair<string, byte[]> pair in tokenToBytes)
		{
			tokens.Add(new TokenDefinition(pair.Key, pair.Value));
		}

		tokens.Sort((left, right) =>
		{
			int lengthCompare = right.Bytes.Length.CompareTo(left.Bytes.Length);
			if (lengthCompare != 0)
			{
				return lengthCompare;
			}

			return string.CompareOrdinal(left.Token, right.Token);
		});

		return tokens;
	}

	private sealed class ScriptEntry
	{
		public ScriptEntry(uint pointerAddress)
		{
			PointerAddress = pointerAddress;
		}

		public uint PointerAddress { get; set; }

		public uint StringAddress { get; set; }

		public int ByteLength { get; set; }

		public bool IsRecovered { get; set; }

		public List<string> Lines { get; } = new List<string>();

		public string Text
		{
			get { return string.Concat(Lines); }
		}
	}

	private sealed class TokenDefinition
	{
		public TokenDefinition(string token, byte[] bytes)
		{
			Token = token;
			Bytes = bytes;
		}

		public string Token { get; }

		public byte[] Bytes { get; }
	}

	private sealed class ExtractionMetadata
	{
		public List<ExtractionEntry> Entries { get; set; } = new List<ExtractionEntry>();
	}

	private sealed class ExtractionEntry
	{
		public uint PointerAddress { get; set; }

		public uint StringAddress { get; set; }

		public int OriginalByteLength { get; set; }

		public bool Recovered { get; set; }
	}

	private sealed class JsonScriptItem
	{
		public string pointerAddress { get; set; }
		public string stringAddress { get; set; }
		public string dialog { get; set; }
		
		public int originalByteLength { get; set; }
		public bool recovered { get; set; }
	}
}
