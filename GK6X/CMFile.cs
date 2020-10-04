using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MiniJSON;

namespace GK6X {
	// These values need to be correct as they form part of the crc calculation
	public enum CMFileType {
        /// <summary>
        ///     0=??? some chinese characters? (CE DE D0 A7 00)
        /// </summary>
        Unknown = 0,

        /// <summary>
        ///     PROFILE
        /// </summary>
        Profile = 1,

        /// <summary>
        ///     LIGHT
        /// </summary>
        Light = 2,

        /// <summary>
        ///     STATASTIC
        /// </summary>
        Statastic = 3,

        /// <summary>
        ///     APPCONF
        /// </summary>
        Appconf = 4,

        /// <summary>
        ///     MACRO
        /// </summary>
        Macro = 5
	}

	internal static class CMFile {
		private const uint fileSignature = 0x434D4631; // Magic / signature "1FMC"

		private static readonly Dictionary<CMFileType, byte[]> fileTypes = new Dictionary<CMFileType, byte[]> {
			{CMFileType.Unknown, new byte[] {0xCE, 0xDE, 0xD0, 0xA7}}, // Chinese letters? "ÎÞÐ§"?
			{CMFileType.Profile, Encoding.ASCII.GetBytes("PROFILE")},
			{CMFileType.Light, Encoding.ASCII.GetBytes("LIGHT")},
			{CMFileType.Statastic, Encoding.ASCII.GetBytes("STATASTIC")},
			{CMFileType.Appconf, Encoding.ASCII.GetBytes("APPCONF")},
			{CMFileType.Macro, Encoding.ASCII.GetBytes("MACRO")}
		};

		// This is a hack... these values are wack, no idea what's going on with them
		private static readonly Dictionary<uint, ushort> unknownMaps = new Dictionary<uint, ushort> {
			{GetUnknownMapStr("ihds"), 25869},
			{GetUnknownMapStr("IHDS"), 3155},
			{0xA7D0DECE, 36218} // "Unknown"
		};

		private static uint GetUnknownMapStr(string header) {
			return BitConverter.ToUInt32(Encoding.ASCII.GetBytes(header), 0);
		}

		public static byte[] Load(string path) {
			if (File.Exists(path)) return Decrypt(File.ReadAllBytes(path), path);
			return null;
		}

		public static byte[] Decrypt(byte[] buffer) {
			return Decrypt(buffer, null);
		}

		private static byte[] Decrypt(byte[] buffer, string file) {
			using (var stream = new MemoryStream(buffer))
			using (var reader = new BinaryReader(stream)) {
				if (reader.ReadUInt32() != fileSignature) {
					Log("Bad file signature", buffer, file);
					return null;
				}

				// Header crc is at offset 4, written as 4 bytes (but still a crc16)
				// (this is a crc of the first 32 bytes (where the crc bytes are 0)
				stream.Position = 4;
				var headerCrc = reader.ReadUInt16();

				// Timestamp is at offset 8, written as 4 bytes
				stream.Position = 8;
				var timestamp = reader.ReadInt32();

				// Length is at offset 12, written as 4 bytes
				stream.Position = 12;
				var dataLength = reader.ReadInt32();

				// Data crc is at offset 16, written as 4 bytes (but still a crc16)
				stream.Position = 16;
				var dataCrc = reader.ReadUInt16();

				// File type is at offset 20, written as 4 bytes
				stream.Position = 20;
				var fileType = reader.ReadInt32();

				// File type (string) is at offset 24, written as 8 bytes, padded with 00
				stream.Position = 24;
				var fileTypeStrBuffer = reader.ReadBytes(8);
				var intFileType = BitConverter.ToUInt32(fileTypeStrBuffer, 0);
				if (unknownMaps.ContainsKey(intFileType)) {
					var newType = BitConverter.GetBytes(unknownMaps[intFileType]);
					fileTypeStrBuffer = new byte[8];
					Buffer.BlockCopy(newType, 0, fileTypeStrBuffer, 0, newType.Length);
				}

				var blob = new List<byte>();
				for (var i = 0; i < fileTypeStrBuffer.Length; i++) {
					if (fileTypeStrBuffer[i] == 0) break;
					blob.Add(fileTypeStrBuffer[i]);
				}

				// First crc the file type name, then get crc the file type name (including zeroed bytes)
				var encryptionKey = Crc16.GetCrc(blob.ToArray());
				encryptionKey = Crc16.GetCrc(fileTypeStrBuffer, 0, encryptionKey);

				// Data is at offset 32
				stream.Position = 32;
				var data = reader.ReadBytes(dataLength);
				var calculatedDataCrc = Decrypt(data, encryptionKey);

				if (dataCrc != calculatedDataCrc) Log("File has an invalid data crc", buffer, file);

				if (stream.Position != stream.Length) Log("File has trailing bytes", buffer, file);

				stream.Position = 0;
				var header = reader.ReadBytes(32);
				header[4] = 0;
				header[5] = 0;
				header[6] = 0;
				header[7] = 0;
				var calculatedHeaderCrc = Crc16.GetCrc(header);
				if (headerCrc != calculatedHeaderCrc) Log("File has an invalid header crc", buffer, file);

				return data;
			}
		}

		public static byte[] Encrypt(byte[] fileData, CMFileType fileType) {
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream)) {
				var fileTypeStrBuffer = new byte[8];
				Buffer.BlockCopy(fileTypes[fileType], 0, fileTypeStrBuffer, 0, fileTypes[fileType].Length);
				var fileTypeStr = Encoding.ASCII.GetString(fileTypeStrBuffer).TrimEnd('\0');
				var encryptionKey = Crc16.GetCrc(Encoding.ASCII.GetBytes(fileTypeStr));
				encryptionKey = Crc16.GetCrc(fileTypeStrBuffer, 0, encryptionKey);

				var encryptedData = new byte[fileData.Length];
				Buffer.BlockCopy(fileData, 0, encryptedData, 0, fileData.Length);
				var dataCrc = Encrypt(encryptedData, encryptionKey);

				// Offset 0 (file signature)
				writer.Write(fileSignature);

				// Offset 4 (header crc - to be built after the header is fully formed)
				writer.Write(0);

				// Offset 8 (timestamp)
				writer.Write(GetTimeStamp(DateTime.Now));

				// Offset 12 (data length)
				writer.Write(fileData.Length);

				// Offset 16 (data crc)
				writer.Write((int) dataCrc);

				// Offset 20 (file type)
				writer.Write((int) fileType);

				// Offset 24-32 (file type string)
				for (var i = 0; i < 8; i++)
					writer.Write((byte) (i < fileTypeStrBuffer.Length ? fileTypeStrBuffer[i] : 0));

				writer.Write(encryptedData);

				// Get the header bytes, calculate the crc, and insert the crc into the header
				var tempPos = stream.Position;
				stream.Position = 0;
				var header = new byte[32];
				stream.Read(header, 0, header.Length);
				var headerCrc = Crc16.GetCrc(header);
				stream.Position = 4;
				writer.Write(headerCrc);
				stream.Position = tempPos;

				return stream.ToArray();
			}
		}

		private static int GetTimeStamp(DateTime dateTime) {
			return (int) dateTime.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
		}

		private static ushort Encrypt(byte[] buffer, ushort key) {
			ushort dataCrc = 0xFFFF;
			for (var i = 0; i < buffer.Length; i++) {
				var tempKey = key;
				key = (ushort) (Crc16.table[buffer[i] ^ (byte) (key >> 8)] ^ (ushort) (key << 8));
				dataCrc = (ushort) (Crc16.table[buffer[i] ^ (byte) (dataCrc >> 8)] ^ (ushort) (dataCrc << 8));
				buffer[i] = (byte) (buffer[i] ^ tempKey);
			}

			return dataCrc;
		}

		private static ushort Decrypt(byte[] buffer, ushort key) {
			ushort dataCrc = 0xFFFF;
			for (var i = 0; i < buffer.Length; i++) {
				buffer[i] = (byte) (buffer[i] ^ key);
				key = (ushort) (Crc16.table[buffer[i] ^ (byte) (key >> 8)] ^ (ushort) (key << 8));
				dataCrc = (ushort) (Crc16.table[buffer[i] ^ (byte) (dataCrc >> 8)] ^ (ushort) (dataCrc << 8));
			}

			return dataCrc;
		}

		private static void Log(string str, byte[] buffer, string file) {
			Debug.WriteLine("[CMFile-ERROR] " + str + " (file: " + file + ")");
		}

		public static void DumpLighting(string path, string dumpPath) {
			if (Directory.Exists(path)) {
				var namesByGuid = new Dictionary<string, string>();
				// Manually add untranslated effect names
				namesByGuid["28E53269-73CC-48c0-B437-C74837B8CD0E"] = "Music volume light effect 2";
				namesByGuid["B3370967-FE81-4733-A54C-1FF3D955E942"] = "fn1 lower lamp position change cherry 1";
				namesByGuid["B70DC715-98F5-40f1-A3A3-86A7E6C95984"] = "Full bright yellow light";
				namesByGuid["CA48BB92-593B-4891-A52F-41E8FB04BF8B"] = "Synchronous RGB gradient";
				namesByGuid["DA3AF708-4B88-4ae8-B92E-DD1221A563CF"] = "Music volume lighting effect";
				namesByGuid["E25817F8-DFF9-4cc5-A393-DC0EF3D4E646"] = "CSGO game lighting effects";

				if (string.IsNullOrEmpty(dumpPath)) dumpPath = Path.Combine(path, "dump");
				Directory.CreateDirectory(dumpPath);

				var leListFile = File.ReadAllText(Path.Combine(path, "lelist_en.json"));
				var leList = Json.Deserialize(leListFile) as List<object>;
				foreach (var item in leList) {
					var guidName = item as Dictionary<string, object>;
					namesByGuid[guidName["GUID"].ToString()] = guidName["Name"].ToString();
				}

				foreach (var file in Directory.GetFiles(path, "*.le")) {
					var str = Encoding.UTF8.GetString(Load(file));

					string leName;
					var guid = Path.GetFileNameWithoutExtension(file).Substring(0, 36);
					namesByGuid.TryGetValue(guid, out leName);

					if (!string.IsNullOrEmpty(leName)) {
						foreach (var c in Path.GetInvalidFileNameChars()) leName = leName.Replace(c, '_');

						var formattedJson = FormatJson(str);
						File.WriteAllText(Path.Combine(dumpPath, leName + ".le"), formattedJson);
					}
					else {
						var json = Json.Deserialize(str) as Dictionary<string, object>;
						Debug.WriteLine("Failed to get name for " + file + " GUID: " + guid + " zh: " + json["Name"]);
					}
				}
			}
		}

		internal static string FormatJson(string json) {
			// https://stackoverflow.com/questions/4580397/json-formatter-in-c/24782322#24782322
			const string INDENT_STRING = "    ";
			var indentation = 0;
			var quoteCount = 0;
			var result =
				from ch in json
				let quotes = ch == '"' ? quoteCount++ : quoteCount
				let lineBreak = ch == ',' && quotes % 2 == 0
					? ch + Environment.NewLine + string.Concat(Enumerable.Repeat(INDENT_STRING, indentation))
					: null
				let openChar = ch == '{' || ch == '['
					? ch + Environment.NewLine + string.Concat(Enumerable.Repeat(INDENT_STRING, ++indentation))
					: ch.ToString()
				let closeChar = ch == '}' || ch == ']'
					? Environment.NewLine + string.Concat(Enumerable.Repeat(INDENT_STRING, --indentation)) + ch
					: ch.ToString()
				select lineBreak == null ? openChar.Length > 1 ? openChar : closeChar : lineBreak;
			return string.Concat(result);
		}
	}
}