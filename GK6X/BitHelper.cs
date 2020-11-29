namespace GK6X {
	internal static class BitHelper {
		public static bool[] BytesToBits(byte[] bytes) {
			var result = new bool[bytes.Length * 8];
			for (var i = 0; i < result.Length; i++) {
				var byteIndex = i / 8;
				var bitIndex = i % 8;
				result[i] = (bytes[byteIndex] & (byte) (1 << bitIndex)) != 0;
			}

			return result;
		}

		public static byte[] BitsToBytes(bool[] bits) {
			var result = new byte[bits.Length / 8];
			for (var i = 0; i < bits.Length; i++)
				if (bits[i]) {
					var byteIndex = i / 8;
					var bitIndex = i % 8;
					result[byteIndex] |= (byte) (1 << bitIndex);
				}

			return result;
		}
	}
}