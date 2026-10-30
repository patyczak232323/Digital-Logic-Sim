using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace DLS.Game.Updates
{
	public enum UpdatePlatform
	{
		Windows,
		Linux,
		Android,
		Other
	}

	// The latest Rewired release on GitHub, read from the releases API
	// (https://api.github.com/repos/<owner>/<repo>/releases/latest).
	public sealed class ReleaseInfo
	{
		public const string Repository = "patyczak232323/Rewired";
		public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
		public const string ReleasesPageUrl = "https://github.com/" + Repository + "/releases/latest";

		public string Tag;
		public string Version; // tag without the leading 'v'
		public string PageUrl;
		public readonly Dictionary<string, (string url, long size)> Assets = new(StringComparer.Ordinal);

		// File names of the release assets, as published by .github/workflows/release.yml.
		public static string AssetName(UpdatePlatform platform) => platform switch
		{
			UpdatePlatform.Windows => "DLSRewired-Windows-x64.zip",
			UpdatePlatform.Linux => "DLSRewired-Linux-x86_64.zip",
			UpdatePlatform.Android => "DLSRewired-Android-arm64.apk",
			_ => null
		};

		public static ReleaseInfo Parse(string json)
		{
			JObject root = JObject.Parse(json);
			string tag = (string)root["tag_name"];
			if (string.IsNullOrWhiteSpace(tag)) throw new FormatException("The release has no tag.");

			ReleaseInfo release = new()
			{
				Tag = tag,
				Version = tag.TrimStart('v', 'V'),
				PageUrl = (string)root["html_url"] ?? ReleasesPageUrl
			};

			if (root["assets"] is JArray assets)
			{
				foreach (JToken asset in assets)
				{
					string name = (string)asset["name"];
					string url = (string)asset["browser_download_url"];
					if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
					release.Assets[name] = (url, (long?)asset["size"] ?? 0);
				}
			}

			return release;
		}

		public bool TryGetAsset(UpdatePlatform platform, out string url, out long size)
		{
			url = null;
			size = 0;
			string name = AssetName(platform);
			if (name == null || !Assets.TryGetValue(name, out (string url, long size) asset)) return false;
			(url, size) = asset;
			return true;
		}

		public bool IsNewerThan(string currentVersion) => CompareVersions(Version, currentVersion) > 0;

		// Compares dotted version numbers ("0.10.0" > "0.9.3"). A pre-release suffix
		// ("0.5.0-beta") sorts before the release itself.
		public static int CompareVersions(string a, string b)
		{
			(int[] numbersA, string suffixA) = Split(a);
			(int[] numbersB, string suffixB) = Split(b);

			for (int i = 0; i < Math.Max(numbersA.Length, numbersB.Length); i++)
			{
				int x = i < numbersA.Length ? numbersA[i] : 0;
				int y = i < numbersB.Length ? numbersB[i] : 0;
				if (x != y) return x.CompareTo(y);
			}

			if (suffixA.Length == 0 || suffixB.Length == 0) return suffixB.Length.CompareTo(suffixA.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };
			return string.CompareOrdinal(suffixA, suffixB);
		}

		static (int[] numbers, string suffix) Split(string version)
		{
			version = (version ?? string.Empty).Trim().TrimStart('v', 'V');
			int dash = version.IndexOfAny(new[] { '-', '+' });
			string suffix = dash >= 0 ? version.Substring(dash + 1) : string.Empty;
			string[] parts = (dash >= 0 ? version.Substring(0, dash) : version).Split('.');

			int[] numbers = new int[parts.Length];
			for (int i = 0; i < parts.Length; i++)
			{
				int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]);
			}

			return (numbers, suffix);
		}
	}
}
