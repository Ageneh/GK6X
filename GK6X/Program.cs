using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using MiniJSON;

namespace GK6X {
	internal class Program {
		public static string BasePath;
		public static string DataBasePath = "Data";
		public static string UserDataPath = "UserData";

		private static readonly object logLocker = new object();

		private static void Main(string[] args) {
			Run(false);
			Stop();
		}

		public static int DllMain(string arg) {
			Run(true);
			Stop();
			return 0;
		}

		private static void Stop() {
			KeyboardDeviceManager.StopListener();
			WebGUI.Stop();
			Environment.Exit(0); // Ensure any loose threads die...
		}

		private static void Run(bool asGUI) {
			BasePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			DataBasePath = Path.Combine(BasePath, DataBasePath);
			UserDataPath = Path.Combine(BasePath, UserDataPath);

			if (!Localization.Load()) {
				LogFatalError("Failed to load localization data");
				return;
			}

			if (!KeyValues.Load()) {
				LogFatalError("Failed to load the key data");
				return;
			}

			if (!KeyboardState.Load()) {
				LogFatalError("Failed to load keyboard data");
				return;
			}

#if COMMAND_LOGGER_ENABLED
            if (args.Length > 0 && args[0].ToLower() == "clog")
            {
                CommandLogger.Run();
                return;
            }
#endif

			KeyboardDeviceManager.Connected += device => {
				Log("Connected to device '" + device.State.ModelName + "' model:" + device.State.ModelId +
				    " fw:" + device.State.FirmwareVersion);
				WebGUI.UpdateDeviceList();

				var file = GetUserDataFile(device);
				if (!string.IsNullOrEmpty(file))
					try {
						var dir = Path.GetDirectoryName(file);
						if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
						if (!File.Exists(file)) File.WriteAllText(file, string.Empty, Encoding.UTF8);
					}
					catch { }
			};
			KeyboardDeviceManager.Disconnected += device => {
				Log("Disconnected from device '" + device.State.ModelName + "'");
				WebGUI.UpdateDeviceList();
			};
			KeyboardDeviceManager.StartListener();

			if (asGUI) {
				WebGUI.Run();
				while (WebGUI.LastPing > DateTime.Now - WebGUI.PingTimeout) Thread.Sleep(1000);
				return;
			}

			var running = true;
			var hasNullInput = false;
			while (running) {
				var line = Console.ReadLine();
				if (line == null) {
					// Handler for potential issue where ReadLine() returns null - see https://github.com/pixeltris/GK6X/issues/8
					if (hasNullInput) {
						Console.WriteLine("Cannot read from command line. Exiting.");
						break;
					}

					hasNullInput = true;
					continue;
				}

				hasNullInput = false;
				var splitted = line.Split();
				switch (splitted[0].ToLower()) {
					case "close":
					case "exit":
					case "quit":
						running = false;
						break;
					case "cls":
					case "clear":
						Console.Clear();
						break;
					case "update_data":
						if (splitted.Length > 1) {
							var path = line.TrimStart();
							var spaceChar = path.IndexOf(' ');
							if (spaceChar > 0) path = path.Substring(spaceChar).Trim();
							var isValidPath = false;
							try {
								if (Directory.Exists(path)) isValidPath = true;
							}
							catch { }

							if (isValidPath) {
								UpdateDataFiles(path);
								Log("done");
							}
							else {
								Log("Couldn't find path '" + path + "'");
							}
						}
						else {
							Log("Bad input. Expected folder name.");
						}

						break;
					case "gui":
						WebGUI.Run();
						break;
					case "gui_le": {
						var userDataPath = WebGUI.UserDataPath;
						var leName = line.Trim();
						var spaceIndex = leName.IndexOf(' ');
						if (spaceIndex > 0)
							leName = leName.Substring(spaceIndex).Trim();
						else
							leName = null;
						if (!string.IsNullOrEmpty(leName)) {
							if (!string.IsNullOrEmpty(userDataPath) && Directory.Exists(userDataPath)) {
								var leDir = Path.Combine(userDataPath, "Account", "0", "LE");
								var leListFile = Path.Combine(leDir, "lelist.json");
								if (File.Exists(leListFile)) {
									var foundFile = false;
									var leList = Json.Deserialize(File.ReadAllText(leListFile)) as List<object>;
									if (leList != null)
										foreach (var item in leList) {
											var guidName = item as Dictionary<string, object>;
											if (guidName["Name"].ToString() == leName) {
												var leFileName = Path.Combine(leDir, guidName["GUID"] + ".le");
												if (File.Exists(leFileName)) {
													foreach (var c in Path.GetInvalidFileNameChars())
														leName = leName.Replace(c, '_');
													var effectString = Encoding.UTF8.GetString(CMFile.Load(leFileName));
													effectString = CMFile.FormatJson(effectString);
													File.WriteAllText(
														Path.Combine(DataBasePath, "lighting", leName + ".le"),
														effectString);
													Log("Copied '" + leName + "'");
													foundFile = true;
												}
											}
										}

									if (!foundFile)
										Log("Failed to find lighting effect '" + leName + "' (it's case sensitive)");
								}
								else {
									Log("Failed to find file '" + leListFile + "'");
								}
							}
						}
						else {
							Log("Invalid input. Expected lighting effect name.");
						}
					}
						break;
					case "findkeys": {
						Log(string.Empty);
						Log(
							"This is used to identify keys. Press keys to see their values. Missing keys will generally show up as '(null)' and they need to be mapped in the data files Data/devuces/YOUR_MODEL_ID/");
						Log("The 'S' values are what you want to use to map keys in your UserData file.");
						Log(string.Empty);
						Log("Entering 'driver' mode and mapping all keys to callbacks.");
						Log(string.Empty);
						KeyboardDevice[] devices;
						if (TryGetDevices(out devices))
							foreach (var device in devices) {
								device.SetLayer(KeyboardLayer.Driver);
								device.SetIdentifyDriverMacros();
							}
					}
						break;
					case "map":
					case "unmap": {
						var map = splitted[0].ToLower() == "map";
						KeyboardDevice[] devices;
						KeyboardLayer targetLayer;
						bool targetLayerIsFn;
						TryParseLayer(splitted, 1, out targetLayer, out targetLayerIsFn);
						var hasTargetLayer = targetLayer != KeyboardLayer.Invalid;
						if (TryGetDevices(out devices))
							foreach (var device in devices) {
								var userData = UserDataFile.Load(device.State, GetUserDataFile(device));
								if (userData == null) {
									Log("Couldn't find user data file '" + GetUserDataFile(device) + "'");
									continue;
								}

								foreach (var layer in device.State.Layers) {
									if (layer.Key == KeyboardLayer.Driver) continue;

									if (hasTargetLayer && layer.Key != targetLayer) continue;

									device.SetLighting(layer.Key, userData);
									device.SetMacros(layer.Key, userData);

									for (var i = 0; i < 2; i++) {
										var fn = i == 1;
										if (targetLayer != KeyboardLayer.Invalid && fn != targetLayerIsFn) continue;

										// Setting keys to 0xFFFFFFFF is preferable compared to using what is defined in 
										// files as this will use what is defined in the firmware.
										var driverValues = new uint[device.State.MaxLogicCode];
										for (var j = 0; j < driverValues.Length; j++)
											driverValues[j] = KeyValues.UnusedKeyValue;

										if (map) {
											UserDataFile.Layer userDataLayer;
											if (fn)
												userData.FnLayers.TryGetValue(layer.Key, out userDataLayer);
											else
												userData.Layers.TryGetValue(layer.Key, out userDataLayer);
											if (userDataLayer != null)
												for (var j = 0; j < driverValues.Length; j++) {
													var key = device.State.GetKeyByLogicCode(j);
													if (key != null) driverValues[j] = userDataLayer.GetKey(key);
												}
										}

										device.SetKeys(layer.Key, driverValues, fn);
									}
								}

								// This is required to "refresh" the keyboard with the updated key info
								if (hasTargetLayer)
									device.SetLayer(targetLayer);
								else
									device.SetLayer(KeyboardLayer.Base);
								Log("Done");
							}
					}
						break;
					case "dumpkeys": {
						var targetRow = -1;
						if (splitted.Length > 1)
							if (!int.TryParse(splitted[1], out targetRow))
								targetRow = -1;
						var showLocationCodeInfo = false;
						if (splitted.Length > 2) showLocationCodeInfo = splitted[2] == "ex";

						KeyboardDevice[] devices;
						if (TryGetDevices(out devices))
							foreach (var device in devices) {
								Log("====== " + device.State.ModelId + " ======");
								var foundKey = false;
								var lastLeft = int.MinValue;
								var row = 1;
								foreach (var key in device.State.KeysByLocationCode.Values.OrderBy(
									x => x.Position.Top).ThenBy(x => x.Position.Left)) {
									if (key.Position.Left >= 0) {
										if (lastLeft > key.Position.Left && foundKey) {
											if (targetRow == -1) Log("--------");
											foundKey = false;
											row++;
										}

										lastLeft = key.Position.Left;
									}

									if (string.IsNullOrEmpty(key.KeyName) || !key.KeyName.StartsWith("LED-")) {
										if (targetRow == -1 || row == targetRow)
											Log(key.KeyName + " = " + key.DriverValueName +
											    (showLocationCodeInfo ? " (" + key.LocationCode + ")" : string.Empty));
										foundKey = true;
									}
								}
							}
					}
						break;
				}
			}
		}

		private static string GetUserDataFile(KeyboardDevice device) {
			return Path.Combine(UserDataPath, device.State.ModelId + ".txt");
		}

		private static bool TryGetDevices(out KeyboardDevice[] devices) {
			devices = KeyboardDeviceManager.GetConnectedDevices();
			if (devices.Length > 0) return true;

			Log("No devices connected!");
			return false;
		}

		private static bool TryParseLayer(string[] args, int index, out KeyboardLayer layer, out bool fn) {
			layer = KeyboardLayer.Invalid;
			fn = false;
			if (args.Length < index) {
				var arg = args[index];
				if (arg.ToLower().StartsWith("fn")) {
					arg = arg.Substring(2);
					fn = true;
				}

				int layerVal;
				if (int.TryParse(args[index], out layerVal))
					switch (layerVal) {
						case 1:
							layer = KeyboardLayer.Layer1;
							break;
						case 2:
							layer = KeyboardLayer.Layer2;
							break;
						case 3:
							layer = KeyboardLayer.Layer3;
							break;
					}
				else
					Enum.TryParse(args[index], true, out layer);
			}

			switch (layer) {
				case KeyboardLayer.Driver:
					layer = KeyboardLayer.Invalid;
					break;
			}

			return layer != KeyboardLayer.Invalid;
		}

		internal static void Log(string str) {
			lock (logLocker) {
				File.AppendAllText(Path.Combine(BasePath, "KbLog.txt"),
					"[" + DateTime.Now.TimeOfDay + "] " + str + Environment.NewLine);
				Console.WriteLine(str);
			}
		}

		private static void LogFatalError(string str) {
			Log(str);
			Console.ReadLine();
			Environment.Exit(1);
		}

		internal static string GetDriverDir(string dir) {
			var rootDir = Path.Combine(dir, "GK6XPlus Driver");
			if (Directory.Exists(rootDir)) dir = rootDir;
			var engineDir = Path.Combine(dir, "CMSEngine");
			if (Directory.Exists(engineDir)) dir = engineDir;
			var driverDir = Path.Combine(dir, "driver");
			if (Directory.Exists(driverDir)) dir = driverDir;
			var deviceDir = Path.Combine(dir, "device");
			if (Directory.Exists(deviceDir) && File.Exists(Path.Combine(deviceDir, "modellist.json"))) return dir;
			return null;
		}

		private static void ReadModelList(string file, Dictionary<string, object> models) {
			if (File.Exists(file)) {
				var objs = Json.Deserialize(File.ReadAllText(file)) as List<object>;
				if (objs != null)
					foreach (var obj in objs) {
						var dict = obj as Dictionary<string, object>;
						if (dict != null && dict.ContainsKey("ModelID")) models[dict["ModelID"].ToString()] = dict;
					}
			}
		}

		private static void UpdateDataFiles(string srcDir) {
			var additionalDirs = new List<string>();
			additionalDirs.Add("Tronsmart Radiant");
			for (var i = 0; i < additionalDirs.Count;) {
				var fullPath = Path.Combine(srcDir, additionalDirs[i]);
				if (Directory.Exists(fullPath))
					additionalDirs[i++] = GetDriverDir(fullPath);
				else
					additionalDirs.RemoveAt(i);
			}

			// Create a merged 'driver' directory containing all the data from all distributors (for the WebGUI)
			var combinedDriverDir = Path.Combine(srcDir, "driver_combined");

			srcDir = GetDriverDir(srcDir);
			if (string.IsNullOrEmpty(srcDir)) return;

			if (!Directory.Exists(combinedDriverDir)) Directory.CreateDirectory(combinedDriverDir);

			var dstDir = Path.Combine(BasePath, "Data");
			var leDir = Path.Combine(srcDir, "res", "data", "le");
			var deviceDir = Path.Combine(srcDir, "device");
			var modelListFile = Path.Combine(deviceDir, "modellist.json");

			// Format these files manually (https://beautifier.io/)
			var indexJsFile = Path.Combine(srcDir, "index.formatted.js");
			var zeroJsFile = Path.Combine(srcDir, "0.formatted.js");
			if (!File.Exists(indexJsFile) || !File.Exists(zeroJsFile)) {
				Log("Couldn't find formatted js files to process!");
				return;
			}

			if (File.Exists(indexJsFile) && File.Exists(zeroJsFile) && Directory.Exists(leDir) &&
			    Directory.Exists(deviceDir) && File.Exists(modelListFile)) {
				var models = new Dictionary<string, object>();

				foreach (var additionalDir in additionalDirs) {
					var additionalLeDir = Path.Combine(additionalDir, "res", "data", "le");
					if (Directory.Exists(additionalLeDir))
						CMFile.DumpLighting(additionalLeDir, Path.Combine(dstDir, "lighting"));

					var additionalDeviceDir = Path.Combine(additionalDir, "device");
					if (Directory.Exists(additionalDeviceDir)) {
						CopyFilesRecursively(new DirectoryInfo(additionalDeviceDir),
							new DirectoryInfo(Path.Combine(dstDir, "device")), false);
						var additionalModelListFile = Path.Combine(additionalDeviceDir, "modellist.json");
						if (File.Exists(additionalModelListFile)) ReadModelList(additionalModelListFile, models);
					}

					CopyFilesRecursively(new DirectoryInfo(additionalDir), new DirectoryInfo(combinedDriverDir), true);
				}

				CMFile.DumpLighting(leDir, Path.Combine(dstDir, "lighting"));
				CopyFilesRecursively(new DirectoryInfo(deviceDir), new DirectoryInfo(Path.Combine(dstDir, "device")),
					false);

				// TODO: Merge json files in /res/data/le/ and /res/data/macro/
				CopyFilesRecursively(new DirectoryInfo(srcDir), new DirectoryInfo(combinedDriverDir), true);

				// Combine modellist.json files
				ReadModelList(modelListFile, models);
				File.WriteAllText(Path.Combine(dstDir, "device", "modellist.json"),
					CMFile.FormatJson(Json.Serialize(models.Values.ToList())));

				var langDir = Path.Combine(dstDir, "i18n", "langs");
				Directory.CreateDirectory(langDir);

				var indexJs = File.ReadAllText(indexJsFile);
				var commonIndex = 0;
				for (var i = 0; i < 2; i++) {
					var langStr = FindContent(indexJs, "common: {", '{', '}', ref commonIndex);
					if (!string.IsNullOrEmpty(langStr))
						File.WriteAllText(Path.Combine(langDir, (i == 0 ? "en" : "zh") + ".json"), langStr);
				}

				var zeroJs = File.ReadAllText(zeroJsFile);
				var keysStr = FindContent(zeroJs, "el-icon-kb-keyboard", '[', ']');
				if (!string.IsNullOrEmpty(keysStr)) File.WriteAllText(Path.Combine(dstDir, "keys.json"), keysStr);
			}
			else {
				Log("Missing directory / file!");
			}
		}

		private static string FindContent(string str, string header, char openBraceChar, char closeBraceChar) {
			var index = 0;
			return FindContent(str, header, openBraceChar, closeBraceChar, ref index);
		}

		private static string FindContent(string str, string header, char openBraceChar, char closeBraceChar,
			ref int index) {
			var braceCount = 0;
			index = str.IndexOf(header, index);
			if (index > 0) {
				while (str[index] != openBraceChar) index--;
				var commonEndIndex = -1;
				for (var j = index; j < str.Length; j++)
					if (str[j] == openBraceChar) {
						braceCount++;
					}
					else if (str[j] == closeBraceChar) {
						braceCount--;
						if (braceCount == 0) {
							commonEndIndex = j + 1;
							break;
						}
					}

				if (commonEndIndex > 0) {
					var result = CleanJson(str.Substring(index, commonEndIndex - index));
					index = commonEndIndex;
					return result;
				}
			}

			return null;
		}

		private static string CleanJson(string json) {
			var lines = json.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);
			var result = new StringBuilder();
			var indentString = "    ";
			var indent = 0;
			char[] braceChars = {'{', '}', '[', ']'};
			for (var j = 0; j < lines.Length; j++) {
				var line = lines[j].TrimStart();
				line = line.Replace("!0", "true");
				if (line.Length > 0 && !braceChars.Contains(line[0])) {
					line = "\"" + line;
					line = line.Insert(line.IndexOf(':'), "\"");
				}

				if (line.Contains("}")) indent--;
				line = string.Concat(Enumerable.Repeat(indentString, indent)) + line;
				if (line.Contains("{")) indent++;
				result.AppendLine(line);
			}

			return result.ToString();
		}

		private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target, bool all) {
			string[] extensions = {"json", "js"};
			foreach (var dir in source.GetDirectories())
				// "res" folder contains some data we don't want
				if (all || dir.Name != "res") {
					if (dir.Name.Contains("新建文件夹")) // "New Folder" (img\新建文件夹\)
						continue;
					// Remove the special case folder (TODO: Make this more generic)
					CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name.Replace("(风控)", string.Empty)), all);
				}

			foreach (var file in source.GetFiles())
				if (all || extensions.Contains(file.Extension.ToLower().TrimStart('.')))
					if (!file.Name.Contains("剑灵") && !file.Name.Contains("逆战") &&
					    !file.Name.Contains("下灯序") && // Data\device\656801861\data\keymap下灯序.js
					    !file.Name.Contains("新建文件夹")) // driver\res\img\新建文件夹.rar
						file.CopyTo(Path.Combine(target.FullName, file.Name), true);
			if (!all && target.GetFiles().Length == 0) target.Delete();
		}
	}
}