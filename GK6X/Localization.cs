using System;
using System.Collections.Generic;
using System.IO;
using MiniJSON;

namespace GK6X {
	public static class Localization {
		public static Dictionary<string, Dictionary<string, string>> Values;
		public static string CurrentLocale = "en";

		public static bool Load() {
			Values = new Dictionary<string, Dictionary<string, string>>();

			var dir = Path.Combine(Program.DataBasePath, "i18n", "langs");
			foreach (var file in Directory.GetFiles(dir, "*.json")) {
				var locale = Path.GetFileNameWithoutExtension(file);

				var localeValues = new Dictionary<string, string>();
				Values[locale] = localeValues;

				var groups = Json.Deserialize(File.ReadAllText(file)) as Dictionary<string, object>;
				if (groups != null)
					foreach (var group in groups) {
						var groupName = group.Key;
						var groupValues = group.Value as Dictionary<string, object>;
						if (groupValues != null)
							foreach (var value in groupValues) {
								if (localeValues.ContainsKey(value.Key))
									Console.WriteLine("[WARNING] Duplicate locale key " + value.Key);
								localeValues[value.Key] = value.Value.ToString();
							}
					}
			}

			return true;
		}

		public static string GetValue(string key) {
			return GetValue(key, CurrentLocale);
		}

		public static string GetValue(string key, string locale) {
			string result;
			TryGetValue(key, out result, locale);
			return result;
		}

		public static bool TryGetValue(string key, out string value) {
			return TryGetValue(key, out value, CurrentLocale);
		}

		public static bool TryGetValue(string key, out string value, string locale) {
			Dictionary<string, string> localeValues;
			if (Values.TryGetValue(locale, out localeValues)) return localeValues.TryGetValue(key, out value);
			value = null;
			return false;
		}
	}

	public class LocalizedString {
		public string KeyName;

		public LocalizedString(string keyName) {
			KeyName = keyName;
		}

		public string Value => GetValue();

		public string GetValue() {
			return Localization.GetValue(KeyName);
		}

		public string GetValue(string locale) {
			return Localization.GetValue(KeyName, locale);
		}

		public bool TryGetValue(out string value) {
			return Localization.TryGetValue(KeyName, out value);
		}

		public bool TryGetValue(out string value, string locale) {
			return Localization.TryGetValue(KeyName, out value, locale);
		}
	}
}