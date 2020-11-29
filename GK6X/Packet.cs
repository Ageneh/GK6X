// Taken from https://github.com/pixeltris/SonyAlphaUSB/blob/master/SonyAlphaUSB/WIALogger.cs
// 30th June 2019

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GK6X {
	public class Packet : IDisposable {
		public static readonly int CharSize = 2;
		private BinaryReader reader;
		private MemoryStream stream;
		private BinaryWriter writer;

		public Packet() {
			stream = new MemoryStream();
			reader = new BinaryReader(stream);
			writer = new BinaryWriter(stream);
		}

		public Packet(OpCodes opcode)
			: this((ushort) opcode) { }

		public Packet(ushort opcode)
			: this() {
			WriteUInt16(opcode);
		}

		public Packet(bool asReader, byte[] data)
			: this() {
			WriteBytes(data);
			if (asReader) Index = 0;
		}

		public Packet(bool asReader, string hexstring)
			: this() {
			WriteHexString(hexstring);
			if (asReader) Index = 0;
		}

		public int Length => (int) stream.Length;

		public int Index {
			get => (int) stream.Position;
			set => SetIndex(value);
		}

		public byte Opcode1 {
			get {
				var tempIndex = Index;
				Index = 0;
				var opcode = reader.ReadByte();
				Index = tempIndex;
				return opcode;
			}
		}

		public byte Opcode2 {
			get {
				var tempIndex = Index;
				Index = 1;
				var opcode = reader.ReadByte();
				Index = tempIndex;
				return opcode;
			}
		}

		public ushort Opcode {
			get {
				var tempIndex = Index;
				Index = 0;
				var opcode = reader.ReadUInt16();
				Index = tempIndex;
				return opcode;
			}
		}

		public bool InvalidRead { get; private set; }

		public void Dispose() {
			CloseStream();
		}

		public static Packet Reader(byte[] data) {
			return new Packet(true, data);
		}

		private void CloseStream() {
			writer.Close();
			reader.Close();
			stream.Close();
		}

		public string GetOpcodeHex() {
			return ToString(0, 2, false);
		}

		public void WriteOpcode(ushort value) {
			var tempIndex = Index;
			Index = 0;
			WriteUInt16(value);
			if (tempIndex > Index) Index = tempIndex;
		}

		public byte[] GetBuffer() {
			return stream.ToArray();
		}

		public byte[] GetWrittenBuffer() {
			var offset = Index > Length ? Length - 1 : Index;
			var result = new byte[offset];
			Buffer.BlockCopy(GetBuffer(), 0, result, 0, offset);
			return result;
		}

		public bool SetIndex(int index) {
			if (index < stream.Length) {
				stream.Position = index;
				return true;
			}

			if (stream.Length > 0)
				stream.Position = stream.Length - 1;
			else
				stream.Position = 0;

			InvalidRead = true;
			return false;
		}

		public bool Skip(int amount) {
			return SetIndex(Index + amount);
		}

		public Packet Reset() {
			Index = 0;
			return this;
		}

		public void Clear() {
			Index = 0;

			CloseStream();
			stream = new MemoryStream();
			reader = new BinaryReader(stream);
			writer = new BinaryWriter(stream);
		}

		public byte ReadByte() {
			if (Index >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadByte();
		}

		public sbyte ReadSByte() {
			if (Index >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadSByte();
		}

		public bool ReadBool() {
			return ReadByte() != 0;
		}

		public short ReadInt16() {
			if (Index + 1 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadInt16();
		}

		public ushort ReadUInt16() {
			if (Index + 1 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadUInt16();
		}

		public int ReadInt32() {
			if (Index + 3 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadInt32();
		}

		public uint ReadUInt32() {
			if (Index + 3 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadUInt32();
		}

		public long ReadInt64() {
			if (Index + 7 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadInt64();
		}

		public ulong ReadUInt64() {
			if (Index + 7 >= Length) {
				InvalidRead = true;
				return 0;
			}

			return reader.ReadUInt64();
		}

		public float ReadSingle() {
			if (Index + 3 >= Length) return 0;
			return reader.ReadSingle();
		}

		public double ReadDouble() {
			if (Index + 7 >= Length) return 0;
			return reader.ReadDouble();
		}

		public byte[] ReadBytes(int count) {
			var invalidRead = Index + count > Length;
			if (count < 0 || invalidRead) {
				if (count > 0 && invalidRead) InvalidRead = true;
				return null;
			}

			return reader.ReadBytes(count);
		}

		public byte[] ReadBytes(int prefixLength, int count) {
			long length = 0;
			switch (prefixLength) {
				case 1:
					length = ReadByte();
					break;
				default:
				case 2:
					length = ReadInt16();
					break;
				case 4:
					length = ReadInt32();
					break;
			}

			return ReadBytes(count);
		}

		public string ReadString() {
			return ReadString(2);
		}

		public string ReadString(int prefixLength) {
			return ReadString(prefixLength, Encoding.Default);
		}

		public string ReadString(int prefixLength, Encoding encoding) {
			var length = 0;
			switch (prefixLength) {
				case 1:
					length = ReadByte();
					break;
				default:
				case 2:
					length = ReadInt16();
					break;
				case 4:
					length = ReadInt32();
					break;
			}

			var totalLength = length * CharSize;
			var invalidRead = Index + totalLength > Length;
			if (length <= 0 || invalidRead) {
				if (length > 0 && invalidRead) InvalidRead = true;
				return string.Empty;
			}

			return encoding.GetString(ReadBytes(totalLength));
		}

		public string ReadFixedString(int length) {
			return ReadFixedString(length, Encoding.Default);
		}

		public string ReadFixedString(int length, Encoding encoding) {
			var totalLength = length * CharSize;
			var invalidRead = Index + totalLength > Length;
			if (length <= 0 || invalidRead) {
				if (length > 0 && invalidRead) InvalidRead = true;
				return string.Empty;
			}

			return encoding.GetString(ReadBytes(totalLength)).TrimEnd('\0');
		}

		public string ReadCString() {
			return ReadCString(Encoding.Default);
		}

		public string ReadCString(Encoding encoding) {
			var tempIndex = Index;
			var length = 0;
			for (var i = Index; i + CharSize <= Length; i += CharSize) {
				length += CharSize;
				if (CharSize == 2) {
					if (ReadInt16() == 0) break;
				}
				else {
					if (ReadByte() == 0) break;
				}
			}

			Index = tempIndex;

			var totalLength = Math.Max(0, length - CharSize);
			return encoding.GetString(ReadBytes(totalLength)).TrimEnd('\0');
		}

		public byte[] ReadRemaining() {
			return ReadBytes(Length - Index);
		}

		public void WriteBool(bool value) {
			writer.Write(value);
		}

		public void WriteByte(byte value) {
			writer.Write(value);
		}

		public void WriteSByte(sbyte value) {
			writer.Write(value);
		}

		public void WriteInt16(short value) {
			writer.Write(value);
		}

		public void WriteUInt16(ushort value) {
			writer.Write(value);
		}

		public void WriteInt32(int value) {
			writer.Write(value);
		}

		public void WriteUInt32(uint value) {
			writer.Write(value);
		}

		public void WriteInt64(long value) {
			writer.Write(value);
		}

		public void WriteUInt64(ulong value) {
			writer.Write(value);
		}

		public void WriteSingle(float value) {
			writer.Write(value);
		}

		public void WriteDouble(double value) {
			writer.Write(value);
		}

		public void WriteString(string value) {
			WriteString(value, Encoding.Default);
		}

		public void WriteString(string value, int prefixLength) {
			WriteString(value, prefixLength);
		}

		public void WriteString(string value, Encoding encoding) {
			WriteString(value, encoding);
		}

		public void WriteString(string value, int prefixLength, Encoding encoding) {
			if (value == null) value = string.Empty;
			var stringBuffer = encoding.GetBytes(value);
			var totalLength = stringBuffer.Length;
			switch (prefixLength) {
				case 1:
					WriteByte((byte) totalLength);
					break;
				default:
				case 2:
					WriteInt16((short) totalLength);
					break;
				case 4:
					WriteInt32(totalLength);
					break;
			}

			WriteBytes(stringBuffer);
		}

		public void WriteFixedString(string value) {
			WriteFixedString(value, Encoding.Default);
		}

		public void WriteFixedString(string value, Encoding encoding) {
			WriteFixedString(value, encoding);
		}

		public void WriteFixedString(string value, int length) {
			WriteFixedString(value, length);
		}

		public void WriteFixedString(string value, int length, Encoding encoding) {
			if (value == null) value = string.Empty;
			var stringBuffer = encoding.GetBytes(value);
			WriteBytes(stringBuffer);

			var remain = length - stringBuffer.Length;
			if (remain > 0) {
				var remaining = new byte[remain];
				WriteBytes(remaining);
			}
		}

		public void WriteCString(string value) {
			WriteCString(value);
		}

		public void WriteCString(string value, Encoding encoding) {
			if (value == null) value = string.Empty;
			var stringBuffer = encoding.GetBytes(value);
			WriteBytes(stringBuffer);

			// Should check if the string contains a null terminator already?
			for (var i = 0; i < CharSize; i++) WriteByte(0);
		}

		public void WriteStringPaddedLeft(string value, char pad, int len) {
			WriteFixedString(value.PadLeft(len, pad));
		}

		public void WriteStringPaddedRight(string value, char pad, int len) {
			WriteFixedString(value.PadRight(len, pad));
		}

		public void WriteBytes(int count) {
			if (Index + count > Length) WriteBytes(new byte[count]);
		}

		public void WriteBytes(byte[] value) {
			if (value == null) return;
			writer.Write(value);
		}

		public void WriteHexString(string hexstring) {
			if (hexstring == null) return;
			int discarded;
			WriteBytes(HexEncoding.GetBytes(hexstring, out discarded));
		}

		public override string ToString() {
			var buffer = GetBuffer();
			var str = new StringBuilder();
			for (var i = 0; i < buffer.Length; i++)
				str.Append(buffer[i].ToString("X2") + " ");
			return str.ToString().Trim();
		}

		public string ToString(bool writtenDataOnly) {
			var buffer = GetBuffer();
			var length = writtenDataOnly ? Length : buffer.Length;
			var str = new StringBuilder();
			for (var i = 0; i < length; i++)
				str.Append(buffer[i].ToString("X2") + " ");
			return str.ToString().Trim();
		}

		public string ToString(int startIndex, int length) {
			return ToString(startIndex, length, false);
		}

		public string ToString(int startIndex, int length, bool reverse) {
			var buffer = GetBuffer();
			var endIndex = startIndex + length > Length ? Length : startIndex + length;
			var str = new StringBuilder();
			if (reverse)
				for (var i = endIndex - 1; i >= startIndex; i--)
					str.Append(buffer[i].ToString("X2") + " ");
			else
				for (var i = startIndex; i < endIndex; i++)
					str.Append(buffer[i].ToString("X2") + " ");
			return str.ToString().Trim();
		}

		public string ToText() {
			var text = new StringBuilder();
			var splitted = ToString().Split();
			foreach (var hexChar in splitted) {
				if (string.IsNullOrEmpty(hexChar))
					continue;
				var byteChar = int.Parse(hexChar, NumberStyles.HexNumber);
				if (byteChar == 0)
					text.Append(" ");
				else
					text.Append((char) byteChar);
			}

			return text.ToString();
		}

		public int GetStringIndex(string search) {
			var buffer = GetBuffer();
			var str = search.Replace(" ", "");
			if (str.Length % 2 != 0)
				return -1;
			var searchLength = str.Length / 2;
			var bytes = new byte[searchLength];
			for (var i = 0; i < searchLength; i++)
				if (str[i] != '?')
					bytes[i] = Convert.ToByte(str.Substring(i, 2), 16);

			for (var i = 0; i < Length - searchLength; i++) {
				var found = true;
				for (var j = 0; j < searchLength; j++)
					if (str[j * 2] != '?' && buffer[i] != bytes[j]) {
						found = false;
						break;
					}

				if (found)
					return i;
			}

			return -1;
		}

		public static bool IsHex(IEnumerable<char> chars) {
			bool isHex;
			foreach (var c in chars) {
				isHex = c >= '0' && c <= '9' ||
				        c >= 'a' && c <= 'f' ||
				        c >= 'A' && c <= 'F' ||
				        c == ' ' || c == '\t' || c == '\r' || c == '\n';

				if (!isHex)
					return false;
			}

			return true;
		}

		public static string ToHexString(byte[] buffer) {
			var str = string.Empty;
			for (var i = 0; i < buffer.Length; i++)
				str += buffer[i].ToString("X2") + " ";
			return str.Trim();
		}

		public static string ToHexStringI16(short value) {
			using (var packet = new Packet()) {
				packet.WriteInt16(value);
				return packet.ToString();
			}
		}

		public static string ToHexStringU16(ushort value) {
			using (var packet = new Packet()) {
				packet.WriteUInt16(value);
				return packet.ToString();
			}
		}

		public static string ToHexStringI32(int value) {
			using (var packet = new Packet()) {
				packet.WriteInt32(value);
				return packet.ToString();
			}
		}

		public static string ToHexStringU32(uint value) {
			using (var packet = new Packet()) {
				packet.WriteUInt32(value);
				return packet.ToString();
			}
		}

		public static byte[] FromHexString(string hex) {
			int discarded;
			return HexEncoding.GetBytes(hex, out discarded);
		}

		private class HexEncoding {
			/* External code from http://www.codeproject.com/KB/recipes/hexencoding.aspx */
			/* Author = neilck http://www.codeproject.com/script/Membership/Profiles.aspx?mid=375133 */
			public static bool IsHexDigit(char c) {
				int numChar;
				var numA = Convert.ToInt32('A');
				var num1 = Convert.ToInt32('0');
				c = char.ToUpper(c);
				numChar = Convert.ToInt32(c);
				if (numChar >= numA && numChar < numA + 6)
					return true;
				if (numChar >= num1 && numChar < num1 + 10)
					return true;
				return false;
			}

			private static byte HexToByte(string hex) {
				if (hex.Length > 2 || hex.Length <= 0)
					throw new ArgumentException("hex must be 1 or 2 characters in length");
				var newByte = byte.Parse(hex, NumberStyles.HexNumber);
				return newByte;
			}

			public static byte[] GetBytes(string hexString, out int discarded) {
				discarded = 0;
				var newString = new StringBuilder();
				char c;
				// remove all none A-F, 0-9, characters
				for (var i = 0; i < hexString.Length; i++) {
					c = hexString[i];
					if (IsHexDigit(c))
						newString.Append(c);
					else
						discarded++;
				}

				// if odd number of characters, discard last character
				if (newString.Length % 2 != 0) {
					discarded++;
					newString = new StringBuilder(newString.ToString().Substring(0, newString.Length - 1));
				}

				var byteLength = newString.Length / 2;
				var bytes = new byte[byteLength];
				string hex;
				var j = 0;
				for (var i = 0; i < bytes.Length; i++) {
					hex = new string(new[] {newString[j], newString[j + 1]});
					bytes[i] = HexToByte(hex);
					j = j + 2;
				}

				return bytes;
			}

			/* End external code */
		}
	}
}