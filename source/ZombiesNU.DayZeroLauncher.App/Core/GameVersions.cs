using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace zombiesnu.DayZeroLauncher.App.Core
{
	public class MetaPlugin
	{
		public MetaPlugin(string ident)
		{
			this.Ident = ident;
		}

		[JsonProperty("ident")]
		public string Ident { get; set; }

		[JsonProperty("addon")]
		public string Addon { get; set; }

		[JsonProperty("name")]
		public string Name { get; set; }

		[JsonProperty("description")]
		public string Description { get; set; }

		public bool IsEnabled { get; set; }
	}

	public class MetaModDetails
	{
		[JsonProperty("addons")]
		public List<MetaAddon> AddOns;

		[JsonProperty("gametypes")]
		public List<MetaGameType> GameTypes;

		[JsonProperty("plugins")]
		public List<MetaPlugin> Plugins;

		public static string GetFileName(string versionString)
		{
			return Path.Combine(UserSettings.ContentMetaPath, versionString + ".json");
		}

		public static MetaModDetails LoadFromFile(string fullPath)
		{
			var modDetails = JsonConvert.DeserializeObject<MetaModDetails>(File.ReadAllText(fullPath));
			return modDetails;
		}
	}

	public class GameVersion
	{
		private static Version GetFileVersion(string arma2OAExePath)
		{
			try
			{
				var versionInfo = FileVersionInfo.GetVersionInfo(arma2OAExePath);
				return Version.Parse(versionInfo.ProductVersion);
			}
			catch (Exception) { return null; }
		}

		public string DirPath = null;
		public string ExePath = null;
		public Version ExeVersion = null;

		public int? BuildNo 
		{
			get
			{
				if (ExeVersion != null)
					return ExeVersion.Revision;

				return null;
			}			
		}

		public GameVersion(string gameDir)
		{
			var dirInfo = new DirectoryInfo(gameDir);
			if (!dirInfo.Exists)
				return;

			DirPath = dirInfo.FullName;
			ExePath = Path.Combine(gameDir, "arma2oa.exe");
			if (!File.Exists(ExePath))
				ExePath = null;
			else
				ExeVersion = GetFileVersion(ExePath);			
		}
	}

	public class GameVersions
	{
		public GameVersion Retail;
		public GameVersion Beta;

		public GameVersions(string oaDir)
		{
			Retail = new GameVersion(oaDir);
			if (Retail.DirPath == null)
				return;

			Beta = new GameVersion(Path.Combine(oaDir, "Expansion\\beta"));
			if (Beta.DirPath == null)
				return;
		}

		public GameVersion BestVersion
		{
			get
			{
				if (Equals(Retail.BuildNo, Beta.BuildNo))
					return Retail;

				if ((Retail.BuildNo ?? 0) > (Beta.BuildNo ?? 0))
					return Retail;

				if ((Beta.BuildNo ?? 0) > 0)
					return Beta;

				if ((Retail.BuildNo ?? 0) > 0)
					return Retail;

				return null;
			}
		}
	}
}