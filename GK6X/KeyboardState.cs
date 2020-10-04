using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MiniJSON;

namespace GK6X {
	public class KeyboardState {
		internal static Dictionary<uint, KeyboardState> KeyboardStatesByModelId = new Dictionary<uint, KeyboardState>();
		private byte bufferSizeA;
		private byte bufferSizeB;

		public uint FirmwareId;
		public byte FirmwareMajorVersion;
		public byte FirmwareMinorVersion;
		public Dictionary<uint, Key> KeysByDriverValue = new Dictionary<uint, Key>();

        /// <summary>
        ///     A unique name for every key. Keys with duplicate DriverValue entries will be given seperate names here.
        /// </summary>
        public Dictionary<string, Key> KeysByDriverValueName = new Dictionary<string, Key>();

		public Dictionary<int, Key> KeysByLocationCode = new Dictionary<int, Key>();
		public Dictionary<int, Key> KeysByLogicCode = new Dictionary<int, Key>();

		public Dictionary<KeyboardLayer, KeyboardStateLayer> Layers =
			new Dictionary<KeyboardLayer, KeyboardStateLayer>();

		public uint ModelId;
		public string ModelName; // This isn't localized anywhere

		public ushort FirmwaresVersionU16 => (ushort) ((FirmwareMajorVersion << 8) | FirmwareMinorVersion);

		public string FirmwareVersion => "v" + FirmwareMajorVersion + "." + FirmwareMinorVersion;

        /// <summary>
        ///     The maximum value for a LogicCode understood by the keyboard.
        ///     NOTE: Some logic codes are unused within this range.
        /// </summary>
        public int MaxLogicCode => bufferSizeA * bufferSizeB;

		public bool HasInitializedBufferes => MaxLogicCode > 0;

		public static bool Load() {
			var modelListPath = Path.Combine(Program.DataBasePath, "device", "modellist.json");
			var models = Json.Deserialize(File.ReadAllText(modelListPath)) as List<object>;
			if (models != null) {
				foreach (var modelObj in models) {
					var model = modelObj as Dictionary<string, object>;
					long modelId;
					long fwid;
					string name;
					string leType;
					string fwidStr;
					if (Json.TryGetValue(model, "ModelID", out modelId) &&
					    Json.TryGetValue(model, "FWID", out fwidStr) &&
					    Json.TryGetValue(model, "Name", out name) &&
					    Json.TryGetValue(model, "LEType", out leType) &&
					    fwidStr.StartsWith("0x") &&
					    long.TryParse(fwidStr.Substring(2), NumberStyles.HexNumber, null, out fwid)) {
						var keyboardState = new KeyboardState();
						keyboardState.ModelId = (uint) modelId;
						keyboardState.FirmwareId = (uint) fwid;
						keyboardState.ModelName = name;
						keyboardState.LoadKeys();
						KeyboardStatesByModelId[keyboardState.ModelId] = keyboardState;
					}
				}

				return true;
			}

			return false;
		}

		public static KeyboardState GetKeyboardState(uint modelId) {
			KeyboardState result;
			KeyboardStatesByModelId.TryGetValue(modelId, out result);
			return result;
		}

		public void InitializeBuffers(byte sizeA, byte sizeB) {
			Layers.Clear();
			bufferSizeA = 0;
			bufferSizeB = 0;

			if (FirmwareId == 0 || ModelId == 0) return;

			bufferSizeA = sizeA;
			bufferSizeB = sizeB;

			CreateFactoryDefaultLayers();
		}

		private void CreateFactoryDefaultLayers() {
			CreateLayer(KeyboardLayer.Base, null);
			CreateLayer(KeyboardLayer.Driver, null);
			CreateLayer(KeyboardLayer.Layer1, null);
			CreateLayer(KeyboardLayer.Layer2, null);
			CreateLayer(KeyboardLayer.Layer3, null);
		}

		private void CreateLayer(KeyboardLayer layer, Dictionary<string, object> modelData) {
			var state = new KeyboardStateLayer();
			Layers[layer] = state;

			var profileLayerName = string.Empty;
			switch (layer) {
				case KeyboardLayer.Driver:
					profileLayerName = "_online_1";
					break;
				case KeyboardLayer.Layer1:
					profileLayerName = "_offline_1";
					break;
				case KeyboardLayer.Layer2:
					profileLayerName = "_offline_2";
					break;
				case KeyboardLayer.Layer3:
					profileLayerName = "_offline_3";
					break;
			}

			var profilePath = Path.Combine(Program.DataBasePath, "device", ModelId.ToString(),
				"data", "profile" + profileLayerName + ".json");

			if (File.Exists(profilePath))
				state.FactoryDefaultModelData =
					Json.Deserialize(File.ReadAllText(profilePath)) as Dictionary<string, object>;

			if (modelData == null) {
				if (state.FactoryDefaultModelData == null) return;
				modelData = state.FactoryDefaultModelData;
			}

			state.KeySet = new uint[bufferSizeA * bufferSizeB];
			state.FnKeySet = new uint[bufferSizeA * bufferSizeB];
			for (var i = 0; i < state.KeySet.Length; ++i) {
				state.KeySet[i] = KeyValues.UnusedKeyValue;
				state.FnKeySet[i] = KeyValues.UnusedKeyValue;
			}

			state.KeyPressLightingEffect = new byte[128];
			for (var i = 0; i < state.KeyPressLightingEffect.Length; ++i) state.KeyPressLightingEffect[i] = 0xFF;

			SetupDriverKeySetBuffer(modelData, "KeySet", state.KeySet);
			SetupDriverKeySetBuffer(modelData, "FnKeySet", state.FnKeySet);

			Dictionary<string, object> deviceLeData;
			if (Json.TryGetValue(modelData, "DeviceLE", out deviceLeData)) {
				List<object> leSetObs;
				if (Json.TryGetValue(deviceLeData, "LESet", out leSetObs)) state.HasLeSet = true;
			}
		}

		private void SetupDriverKeySetBuffer(Dictionary<string, object> modelData, string type,
			uint[] driverKeySetArray) {
			List<object> keySetData;
			if (Json.TryGetValue(modelData, type, out keySetData))
				foreach (var keySetObj in keySetData) {
					var keySet = keySetObj as Dictionary<string, object>;
					long index;
					long driverValue;
					string driverValueStr;
					if (Json.TryGetValue(keySet, "Index", out index) &&
					    Json.TryGetValue(keySet, "DriverValue", out driverValueStr) &&
					    driverValueStr.StartsWith("0x") &&
					    long.TryParse(driverValueStr.Substring(2), NumberStyles.HexNumber, null, out driverValue)) {
						// 167968769 / 167837697
						if (driverValue == 0xA030001 || driverValue == 0xA010001)
							// Not 100% sure if this is exactly what the regular code does
							driverValue += index;
						driverKeySetArray[index] = (uint) driverValue;
					}
				}
		}

        /// <summary>
        ///     Returns a DriverValue where each index specifies the LogicCode
        /// </summary>
        private uint[] GetDefaultProfileDriverValues() {
			var profilePath = Path.Combine(Program.DataBasePath, "device", ModelId.ToString(), "data", "profile.json");
			if (File.Exists(profilePath)) {
				var modelData = Json.Deserialize(File.ReadAllText(profilePath)) as Dictionary<string, object>;
				var result = Enumerable.Repeat(KeyValues.UnusedKeyValue, short.MaxValue)
					.ToArray(); // Buffer size is unknown at this point
				SetupDriverKeySetBuffer(modelData, "KeySet", result);
				return result;
			}

			return null;
		}

		private void LoadKeys() {
			KeysByLocationCode.Clear();
			KeysByLogicCode.Clear();
			KeysByDriverValue.Clear();
			KeysByDriverValueName.Clear();

			var driverValues = GetDefaultProfileDriverValues();

			var keysPath = Path.Combine(Program.DataBasePath, "device", ModelId.ToString(), "data", "keymap.js");
			if (File.Exists(keysPath)) {
				var deviceKeys = Json.Deserialize(File.ReadAllText(keysPath)) as List<object>;
				foreach (var deviceKeyObj in deviceKeys) {
					var deviceKey = deviceKeyObj as Dictionary<string, object>;
					if (deviceKey != null) {
						var key = new Key();
						long locationCode;
						long logicCode;
						Json.TryGetValue(deviceKey, "KeyName", out key.KeyName);
						Json.TryGetValue(deviceKey, "Show", out key.Show);
						if (Json.TryGetValue(deviceKey, "LocationCode", out locationCode))
							key.LocationCode = (int) locationCode;
						if (Json.TryGetValue(deviceKey, "LogicCode", out logicCode)) key.LogicCode = (int) logicCode;
						Dictionary<string, object> positionInfo;
						if (Json.TryGetValue(deviceKey, "Position", out positionInfo)) {
							long val;
							if (Json.TryGetValue(positionInfo, "Left", out val)) key.Position.Left = (int) val;
							if (Json.TryGetValue(positionInfo, "Top", out val)) key.Position.Top = (int) val;
							if (Json.TryGetValue(positionInfo, "Width", out val)) key.Position.Width = (int) val;
							if (Json.TryGetValue(positionInfo, "Height", out val)) key.Position.Height = (int) val;
						}

						if (key.LogicCode >= 0 && driverValues[key.LogicCode] != KeyValues.UnusedKeyValue) {
							key.DriverValue = driverValues[key.LogicCode];
						}
						else {
							// LogicCode of -1 is assumed to be the Fn key
							if (key.LogicCode > 0) {
								KeyValues.Key allKeysKey;
								if (KeyValues.KeysByLogicCode.TryGetValue(key.LogicCode, out allKeysKey)) {
									key.DriverValue = allKeysKey.DriverValue;
								}
								else {
									Debug.WriteLine("Couldn't find DriverValue for key '" + key.KeyName +
									                "' logicCode: " + key.LogicCode +
									                " locationCode: " + key.LocationCode + " modelId: " + ModelId +
									                " modelName: " + ModelName);
									key.DriverValue = KeyValues.UnusedKeyValue;
								}
							}
							else {
								key.DriverValue = KeyValues.UnusedKeyValue;
							}
						}

						KeysByLocationCode[key.LocationCode] = key;
						KeysByLogicCode[key.LogicCode] = key;
						KeysByDriverValue[key.DriverValue] = key;
						for (var i = 1; i < int.MaxValue; i++) {
							var driverValueName = (DriverValue) key.DriverValue + (i > 1 ? "_" + i : string.Empty);
							if (!KeysByDriverValueName.ContainsKey(driverValueName)) {
								key.DriverValueName = driverValueName;
								KeysByDriverValueName.Add(driverValueName, key);
								break;
							}
						}
					}
				}
			}
		}

		public KeyboardStateLayer GetLayer(KeyboardLayer layer) {
			KeyboardStateLayer result;
			Layers.TryGetValue(layer, out result);
			return result;
		}

		public Key GetKeyAtLocationCode(int locationCode) {
			Key result;
			KeysByLocationCode.TryGetValue(locationCode, out result);
			return result;
		}

		public Key GetKeyByLogicCode(int logicCode) {
			Key result;
			KeysByLogicCode.TryGetValue(logicCode, out result);
			return result;
		}

		public class Key {
			public uint DriverValue;

            /// <summary>
            ///     Unique for a given key, even if there are keys with duplicate driver values
            /// </summary>
            public string DriverValueName;

			public string KeyName;
			public int LocationCode;
			public int LogicCode;
			public KeyRect Position;
			public string Show;
		}
	}

	public struct KeyRect {
		public int Left;
		public int Top;
		public int Width;
		public int Height;
	}

	public class KeyboardStateLayer {
		public Dictionary<string, object> FactoryDefaultModelData;
		public uint[] FnKeySet;

        /// <summary>
        ///     Only used for "driver" layer? (17 01) <see cref="OpCodes.DriverLayerSetConfig" />
        /// </summary>
        public bool HasLeSet;

		public byte[] KeyPressLightingEffect;
		public uint[] KeySet;
	}
}