﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Client.Encryption;
using MonoTorrent.Client.Tracker;
using MonoTorrent.Common;
using MonoTorrent.Dht;
using MonoTorrent.Dht.Listeners;

namespace zombiesnu.DayZeroLauncher.App.Core
{
	internal class TorrentUpdater
	{
		//The engine used for downloading, has to be static because we will only use one port
		public delegate void StatusUpdate(TorrentState currState, double currProgress);

		private static ClientEngine globalEngine;
		private static int engineListenPort;

		public static StatusUpdate StatusCallbacks = (TorrentState currState, double currProgress) => { };
		private static readonly HashSet<int> portsToMap = new HashSet<int>();
		private static readonly HashSet<int> portsMapped = new HashSet<int>();
		private static List<INatDevice> upnpDevices;

		private readonly List<AddOnTorrent> addOnTorrents;

		private readonly TorrentLauncher downloader;
		private readonly bool errorMsgsOnly;
		private readonly bool fullSystemCheck;
		private readonly DayZUpdater updater;
		private readonly string versionString;

		public TorrentUpdater(string versionString, List<MetaAddon> addOns, bool fullSystemCheck, TorrentLauncher downloader,
			DayZUpdater updater, bool errorMsgsOnly)
		{
			addOnTorrents = new List<AddOnTorrent>();
			this.versionString = versionString;
			this.fullSystemCheck = fullSystemCheck;
			this.downloader = downloader;
			this.updater = updater;
			this.errorMsgsOnly = errorMsgsOnly;

			string torrentsDir = null;
			try
			{
				torrentsDir = Path.Combine(UserSettings.ContentMetaPath, versionString);
				{
					var dirInfo = new DirectoryInfo(torrentsDir);
					if (!dirInfo.Exists)
						dirInfo = Directory.CreateDirectory(torrentsDir);
				}
			}
			catch (Exception ex)
			{
				updater.Status = "Error creating torrents directory";
				downloader.Status = ex.Message;
				downloader.IsRunning = false;

				return;
			}

			foreach (MetaAddon addOn in addOns)
			{
				var newAddOn = new AddOnTorrent();
				newAddOn.Meta = addOn;
				newAddOn.torrentFileName = Path.Combine(torrentsDir,
					newAddOn.Meta.Description + "-" + newAddOn.Meta.Version + ".torrent");
				newAddOn.torrentSavePath = null; //will be filled in if successfull download
				addOnTorrents.Add(newAddOn);
			}

			//delete .torrent files that do not match the ones we want
			string[] allTorrents = Directory.GetFiles(torrentsDir, "*.torrent", SearchOption.TopDirectoryOnly);
			foreach (string torrentPath in allTorrents)
			{
				if (
					addOnTorrents.Count(
						naot => { return naot.torrentFileName.Equals(torrentPath, StringComparison.InvariantCultureIgnoreCase); }) < 1)
				{
					//this is an unwanted torrent file
					try
					{
						var fileInfo = new FileInfo(torrentPath);
						if (fileInfo.IsReadOnly)
						{
							fileInfo.IsReadOnly = false;
							fileInfo.Refresh();
						}
						fileInfo.Delete();
					}
					catch (Exception)
					{
					}
				}
			}

			for (int i = 0; i < addOnTorrents.Count; i++)
			{
				int idxCopy = i;
				AddOnTorrent newAddOn = addOnTorrents[i];
				try
				{
					var wc = new HashWebClient();
					wc.DownloadFileCompleted += (sender, args) => { TorrentDownloadComplete(sender, args, idxCopy); };
					wc.BeginDownload(newAddOn.Meta.Torrent, newAddOn.torrentFileName);
				}
				catch (Exception ex)
				{
					updater.Status = "Error starting torrent file download";
					downloader.Status = ex.Message;
					downloader.IsRunning = false;
					return;
				}
			}
		}

		public static TorrentState CurrentState()
		{
			if (globalEngine == null)
				return TorrentState.Stopped;

			IList<TorrentManager> engineTorrents = globalEngine.Torrents;
			if (engineTorrents == null || engineTorrents.Count < 1)
				return TorrentState.Stopped;

			if (engineTorrents.Count(m => m.State == TorrentState.Downloading) > 0)
				return TorrentState.Downloading;
			if (engineTorrents.Count(m => m.State == TorrentState.Hashing) > 0)
				return TorrentState.Hashing;

			return TorrentState.Stopped;
		}

		public static int GetCurrentSpeed()
		{
			if (globalEngine == null)
				return 0;

			IList<TorrentManager> engineTorrents = globalEngine.Torrents;
			if (engineTorrents == null || engineTorrents.Count < 1)
				return 0;

			int totalDownloadSpeed = 0;
			foreach (TorrentManager tm in engineTorrents)
				totalDownloadSpeed += tm.Monitor.DownloadSpeed;

			return totalDownloadSpeed/1024;
		}

		public static double GetCurrentProgress()
		{
			double totalBytes = 0.0;
			double downloadedBytes = 0.0;
			if (globalEngine == null)
				return totalBytes;

			IList<TorrentManager> engineTorrents = globalEngine.Torrents;
			if (engineTorrents == null || engineTorrents.Count < 1)
				return totalBytes;

			foreach (TorrentManager m in engineTorrents)
			{
				totalBytes += m.Torrent.Size;
				if (m.Progress > 0)
					downloadedBytes += (m.Torrent.Size/100.0)*m.Progress;
			}

			return downloadedBytes/totalBytes;
		}

		private void TorrentDownloadComplete(Object sender, AsyncCompletedEventArgs args, int addOnIndex)
		{
			string errMsg = null;
			if (args.Cancelled)
				errMsg = "Async operation cancelled";
			else if (args.Error != null)
				errMsg = args.Error.Message;

			if (errMsg != null)
			{
				updater.Status = "Torrent file download error";
				downloader.Status = errMsg;
				downloader.IsRunning = false;
				return;
			}

			AddOnTorrent addOnStuff = addOnTorrents[addOnIndex];
			addOnStuff.torrentSavePath = Path.Combine(UserSettings.ContentDataPath, addOnStuff.Meta.Name);

			if (addOnTorrents.Count(aot => { return string.IsNullOrWhiteSpace(aot.torrentSavePath); }) < 1)
			{
				//this was the last one, and all of them succeeded
				StartTorrentsThread();
			}
		}

		private static string GetDhtNodesFileName()
		{
			return Path.Combine(UserSettings.TorrentJunkPath, "dht.nodes");
		}

		private static string GetFastResumeFileName(TorrentManager tm)
		{
			return Path.Combine(UserSettings.TorrentJunkPath, "fastresume_" + tm.InfoHash.ToHex() + ".benc");
		}

		private void StartTorrentsThread()
		{
			Task.Factory.StartNew(() => RunTorrents());
		}

		private void RunTorrents()
		{
			TorrentOptions tOpts = UserSettings.Current.TorrentOptions;
			if (globalEngine == null)
			{
				int listenPort = tOpts.ListeningPort;
				string mainDownloadsPath = UserSettings.ContentDataPath;

				// Create the settings which the engine will use
				// downloadsPath - this is the path where we will save all the files to
				// port - this is the port we listen for connections on
				var engineSettings = new EngineSettings(mainDownloadsPath, listenPort);
				engineSettings.PreferEncryption = true;
				engineSettings.AllowedEncryption = EncryptionTypes.All;
				engineSettings.GlobalMaxConnections = tOpts.MaxDLConnsNormalized;
				engineSettings.GlobalMaxDownloadSpeed = tOpts.MaxDLSpeed*1024;
				engineSettings.GlobalMaxHalfOpenConnections = 10;
				engineSettings.GlobalMaxUploadSpeed = tOpts.MaxULSpeed*1024;

				// Create an instance of the engine.
				globalEngine = new ClientEngine(engineSettings);
				globalEngine.ChangeListenEndpoint(new IPEndPoint(IPAddress.Any, listenPort));
				engineListenPort = listenPort;

				EngineStartedOnPort(engineListenPort);

				//create a DHT engine and register it with the main engine
				{
					var dhtListener = new DhtListener(new IPEndPoint(IPAddress.Any, listenPort));
					var dhtEngine = new DhtEngine(dhtListener);
					dhtListener.Start();

					string dhtNodesFileName = "";
					byte[] dhtNodesData = null;
					try
					{
						dhtNodesFileName = GetDhtNodesFileName();
						if (File.Exists(dhtNodesFileName))
							dhtNodesData = File.ReadAllBytes(dhtNodesFileName);
					}
					catch (Exception ex)
					{
						Console.WriteLine("Error loading dht nodes file '{0}', reason: {1}", dhtNodesFileName, ex.Message);
						dhtNodesData = null;
					}

					dhtEngine.Start(dhtNodesData);
					globalEngine.RegisterDht(dhtEngine);

					// We need to cleanup correctly when the user closes the window by using ctrl-c
					// or an unhandled exception happens
					Console.CancelKeyPress += delegate { EngineShutdown(); };
					AppDomain.CurrentDomain.ProcessExit += delegate { EngineShutdown(); };
					AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
					{
						Console.WriteLine(e.ExceptionObject);
						EngineShutdown();
					};
					Thread.GetDomain().UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
					{
						Console.WriteLine(e.ExceptionObject);
						EngineShutdown();
					};
				}
			}
			else
				StopAllTorrents();

			// Create the default settings which a torrent will have.
			var torrentDefaults = new TorrentSettings(tOpts.NumULSlotsNormalized);
			torrentDefaults.UseDht = true;
			torrentDefaults.EnablePeerExchange = true;

			// For each file in the torrents path that is a .torrent file, load it into the engine.
			var managers = new List<TorrentManager>();
			foreach (AddOnTorrent newAddOn in addOnTorrents)
			{
				Torrent torrent = null;
				try
				{
					torrent = Torrent.Load(File.ReadAllBytes(newAddOn.torrentFileName));
				}
				catch (Exception ex)
				{
					updater.Status = "Error loading torrent file";
					downloader.Status = ex.Message;
					downloader.IsRunning = false;
					return;
				}

				try
				{
					var fullFilePaths = new List<String>();
					{
						TorrentFile[] torrentFiles = torrent.Files;
						foreach (TorrentFile theFile in torrentFiles)
							fullFilePaths.Add(Path.Combine(newAddOn.torrentSavePath, theFile.Path));
					}
					if (Directory.Exists(newAddOn.torrentSavePath))
					{
						string[] actualFilePaths = Directory.GetFiles(newAddOn.torrentSavePath, "*.*", SearchOption.AllDirectories);
						foreach (string realPath in actualFilePaths)
						{
							var fileInfo = new FileInfo(realPath);
							if (
								fullFilePaths.Count(
									path => { return path.Equals(fileInfo.FullName, StringComparison.InvariantCultureIgnoreCase); }) < 1)
							{
								//this is an unwanted file
								if (fileInfo.IsReadOnly)
								{
									fileInfo.IsReadOnly = false;
									fileInfo.Refresh();
								}
								fileInfo.Delete();
							}
						}
					}
				}
				catch (Exception ex)
				{
					updater.Status = "Error deleting unwanted file";
					downloader.Status = ex.Message;
					downloader.IsRunning = false;
					return;
				}

				TorrentManager tm = null;
				try
				{
					tm = new TorrentManager(torrent, globalEngine.Settings.SavePath, torrentDefaults, newAddOn.torrentSavePath);
					if (!fullSystemCheck && !UserSettings.Current.TorrentOptions.DisableFastResume)
						//load the fast resume file for this torrent
					{
						string fastResumeFilepath = GetFastResumeFileName(tm);
						if (File.Exists(fastResumeFilepath))
						{
							var bencoded = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(fastResumeFilepath));
							tm.LoadFastResume(new FastResume(bencoded));
						}
					}
				}
				catch (Exception ex)
				{
					if (tm == null) //only if critical failure
					{
						updater.Status = "Error creating torrent manager";
						downloader.Status = ex.Message;
						downloader.IsRunning = false;
						return;
					}
				}

				managers.Add(tm);
			}

			// If we loaded no torrents, just stop.
			if (managers.Count < 1)
			{
				updater.Status = "Torrent engine error";
				downloader.Status = "No torrents have been found";
				downloader.IsRunning = false;
				return;
			}

			try
			{
				File.WriteAllText(UserSettings.ContentCurrentTagFile, versionString);
				CalculatedGameSettings.Current.Update();
			}
			catch (Exception ex)
			{
				updater.Status = "Tag file write error";
				downloader.Status = ex.Message;
				downloader.IsRunning = false;
				return;
			}

			// Before starting all the managers, clear out the fastresume data
			// The torrents currently running already have it loaded, and will save it out on stop/finish
			// So this only clears out fastresume for torrents we aren't currently running, which is what we want
			IEnumerable<string> staleFastResume = Directory.EnumerateFiles(UserSettings.TorrentJunkPath, "fastresume_*.benc",
				SearchOption.TopDirectoryOnly);
			foreach (string sfr in staleFastResume)
			{
				try
				{
					File.Delete(sfr);
				}
				catch (Exception)
				{
				}
			}

			foreach (TorrentManager manager in managers)
			{
				// Add this manager to the global torrent engine
				globalEngine.Register(manager);

				// Every time a new peer is added, this is fired.
				manager.PeersFound += delegate { };
				// Every time a piece is hashed, this is fired.
				manager.PieceHashed += delegate { };
				// Every time the state changes (Stopped -> Seeding -> Downloading -> Hashing) this is fired
				manager.TorrentStateChanged += OnTorrentStateChanged;
				// Every time the tracker's state changes, this is fired
				foreach (TrackerTier tier in manager.TrackerManager)
				{
				}

				manager.Start();
			}

			// While the torrents are still running, print out some stats to the screen.
			// Details for all the loaded torrent managers are shown.
			int i = 0;
			bool running = true;
			var sb = new StringBuilder(1024);
			DateTime lastAnnounce = DateTime.Now;
			bool firstRun = true;
			while (running && globalEngine != null)
			{
				IList<TorrentManager> engineTorrents = globalEngine.Torrents;
				if (firstRun || lastAnnounce < DateTime.Now.AddMinutes(-1))
				{
					foreach (TorrentManager tm in engineTorrents)
						tm.TrackerManager.Announce();

					lastAnnounce = DateTime.Now;
					firstRun = false;
				}

				if ((i++)%2 == 0)
				{
					sb.Remove(0, sb.Length);
					running = engineTorrents.Count(m => { return m.State != TorrentState.Stopped; }) > 0;

					var totalState = TorrentState.Stopped;
					if (engineTorrents.Count(m => m.State == TorrentState.Hashing) > 0)
						totalState = TorrentState.Hashing;
					else if (engineTorrents.Count(m => m.State == TorrentState.Downloading) > 0)
						totalState = TorrentState.Downloading;
					else if (engineTorrents.Count(m => m.State == TorrentState.Seeding) > 0)
						totalState = TorrentState.Seeding;

					string status = "";
					try
					{
						switch (totalState)
						{
							case TorrentState.Hashing:
							{
								double totalHashingBytes = 0;
								double totalHashedBytes = 0;
								foreach (TorrentManager m in engineTorrents)
								{
									totalHashingBytes += m.Torrent.Size;
									if (m.State == TorrentState.Hashing)
										totalHashedBytes += m.Torrent.Size*(m.Progress/100);
									else
										totalHashedBytes += m.Torrent.Size;
								}
								double totalHashProgress = totalHashedBytes/totalHashingBytes;
								status = String.Format("Checking files ({0:0.00}%)", totalHashProgress*100);

								StatusCallbacks(TorrentState.Hashing, totalHashProgress);
							}
								break;
							case TorrentState.Downloading:
							{
								double totalToDownload = 0;
								double totalDownloaded = 0;
								double totalDownloadSpeed = 0;
								double totalUploadSpeed = 0;
								int totalDownloadConns = 0;
								int totalUploadConns = 0;
								foreach (TorrentManager m in engineTorrents)
								{
									totalToDownload += m.Torrent.Size;
									totalDownloaded += m.Torrent.Size*(m.Progress/100);
									totalDownloadSpeed += m.Monitor.DownloadSpeed;
									totalUploadSpeed += m.Monitor.UploadSpeed;
									totalDownloadConns += m.OpenConnections;
									totalUploadConns += m.UploadingTo;
								}
								double totalDownloadProgress = totalDownloaded/totalToDownload;
								status = "Status: " +
								         ((engineTorrents.Count(m => m.State == TorrentState.Downloading && m.GetPeers().Count > 0) > 0)
									         ? "Downloading"
									         : "Finding peers");
								status += "\n" + String.Format("Progress: {0:0.00}%", totalDownloadProgress*100);
								status += "\n" + String.Format("Download({1}): {0:0.00} KiB/s", totalDownloadSpeed/1024.0, totalDownloadConns);
								status += "\n" + String.Format("Upload({1}): {0:0.00} KiB/s", totalUploadSpeed/1024.0, totalUploadConns);

								StatusCallbacks(TorrentState.Downloading, totalDownloadProgress);
							}
								break;
							case TorrentState.Seeding:
							{
								double totalUploadSpeed = 0;
								int totalUploadPeers = 0;
								foreach (TorrentManager tm in engineTorrents)
								{
									totalUploadSpeed += tm.Monitor.UploadSpeed;
									totalUploadPeers += tm.UploadingTo;
								}
								status = String.Format("Seeding({1}): {0:0.00} KiB/s", totalUploadSpeed/1024.0, totalUploadPeers);
								StatusCallbacks(TorrentState.Seeding, 1);

								if (UserSettings.Current.TorrentOptions.StopSeeding)
									globalEngine.StopAll();
							}
								break;
							default:
								status = totalState.ToString();
								break;
						}
					}
					catch (Exception ex)
					{
						status = ex.Message;
					}

					if (downloader != null)
						downloader.Status = status;
				}

				Thread.Sleep(50);
			}
		}

		public void OnTorrentStateChanged(object sender, TorrentStateChangedEventArgs e)
		{
			if (e.NewState == TorrentState.Stopped)
			{
				try
				{
					string resumeDataFileName = GetFastResumeFileName(e.TorrentManager);
					using (FileStream resumeFile = File.OpenWrite(resumeDataFileName))
					{
						e.TorrentManager.SaveFastResume().Encode(resumeFile);
						resumeFile.Flush();
						resumeFile.Close();
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine("Error saving fastresume info for {0}, reason: {1}", e.TorrentManager.InfoHash.ToHex(),
						ex.Message);
				}
			}

			if (e.NewState == TorrentState.Error || e.NewState == TorrentState.Stopped)
			{
				TorrentManager tm = e.TorrentManager;
				ClientEngine engine = tm.Engine;
				engine.Unregister(tm);
			}
			else if (e.TorrentManager.Progress == 100.0 && e.NewState == TorrentState.Seeding)
			{
				TorrentManager tm = e.TorrentManager;
				ClientEngine engine = tm.Engine;
				IList<TorrentManager> engineTorrents = engine.Torrents;

				bool allSeeding = engineTorrents.Count(m => { return (m.Progress < 100 || m.State != TorrentState.Seeding); }) < 1;
				if (allSeeding)
				{
					if (updater != null && !errorMsgsOnly)
						updater.Status = DayZeroLauncherUpdater.STATUS_UPTODATE;

					if (downloader != null)
						downloader.IsRunning = false;

					StatusCallbacks(TorrentState.Stopped, 1);
				}
			}
		}

		private static void InternalMapPort(INatDevice device, int port)
		{
			for (int i = 0; i < 2; i++)
			{
				var proto = Protocol.Tcp;
				if (i > 0)
					proto = Protocol.Udp;

				Mapping mapping = device.GetSpecificMapping(proto, port);
				if (mapping == null || mapping.IsExpired() || mapping.PrivatePort < 0 || mapping.PublicPort < 0)
					device.CreatePortMap(new Mapping(proto, port, port));
			}
		}

		private static void InternalUnMapPort(INatDevice device, int port)
		{
			for (int i = 0; i < 2; i++)
			{
				var proto = Protocol.Tcp;
				if (i > 0)
					proto = Protocol.Udp;

				Mapping mapping = device.GetSpecificMapping(proto, port);
				if (mapping != null && mapping.PrivatePort > 0 && mapping.PublicPort > 0)
					device.DeletePortMap(new Mapping(proto, port, port));
			}
		}

		private static void UpnpDeviceFound(object sender, DeviceEventArgs args)
		{
			INatDevice device = args.Device;
			if (upnpDevices.Contains(device))
				return;

			foreach (int port in portsToMap)
			{
				InternalMapPort(device, port);
				portsMapped.Add(port);
			}

			upnpDevices.Add(device);
		}

		private static void UpnpDeviceLost(object sender, DeviceEventArgs args)
		{
			INatDevice device = args.Device;
			if (upnpDevices.Contains(device))
			{
				if (upnpDevices.Count == 1) //this is the last device
				{
					foreach (int port in portsMapped)
						portsToMap.Add(port);

					portsMapped.Clear();
				}

				upnpDevices.Remove(device);
			}
		}

		private static void InitializeUpnp()
		{
			if (upnpDevices == null)
			{
				upnpDevices = new List<INatDevice>();
				NatUtility.DeviceFound += UpnpDeviceFound;
				NatUtility.DeviceLost += UpnpDeviceLost;

				NatUtility.StartDiscovery();
			}
		}

		private static void DestroyUpnp()
		{
			if (upnpDevices != null)
			{
				NatUtility.StopDiscovery();
				NatUtility.DeviceFound -= UpnpDeviceFound;
				NatUtility.DeviceLost -= UpnpDeviceLost;

				foreach (INatDevice device in upnpDevices)
				{
					foreach (int port in portsMapped)
					{
						InternalUnMapPort(device, port);
						portsToMap.Add(port);
					}
				}
				portsMapped.Clear();
				upnpDevices.Clear();
				upnpDevices = null;
			}
		}

		private static void EngineStartedOnPort(int portNumber)
		{
			if (UserSettings.Current.TorrentOptions.EnableUpnp)
			{
				InitializeUpnp();

				if (upnpDevices != null)
				{
					if (upnpDevices.Count > 0)
					{
						foreach (INatDevice device in upnpDevices)
							InternalMapPort(device, portNumber);

						portsMapped.Add(portNumber);
					}
				}
			}

			portsToMap.Add(portNumber);
		}

		private static void EngineStoppedOnPort(int portNumber)
		{
			portsToMap.Remove(portNumber);

			if (portsMapped.Contains(portNumber) && upnpDevices != null)
			{
				foreach (INatDevice device in upnpDevices)
					InternalUnMapPort(device, portNumber);

				portsMapped.Remove(portNumber);
			}
		}

		public static void ReconfigureEngine()
		{
			if (globalEngine != null)
			{
				TorrentOptions tOpts = UserSettings.Current.TorrentOptions;
				if (engineListenPort != tOpts.ListeningPort)
				{
					byte[] dhtNodesData = null;
					{
						IDhtEngine oldDhtEngine = globalEngine.DhtEngine;
						if (oldDhtEngine != null)
						{
							dhtNodesData = oldDhtEngine.SaveNodes();
							oldDhtEngine.Stop();

							globalEngine.RegisterDht(null);
							if (!oldDhtEngine.Disposed)
								oldDhtEngine.Dispose();
						}
						oldDhtEngine = null;
					}

					EngineStoppedOnPort(engineListenPort);

					engineListenPort = tOpts.ListeningPort;
					globalEngine.ChangeListenEndpoint(new IPEndPoint(IPAddress.Any, engineListenPort));

					var dhtListener = new DhtListener(new IPEndPoint(IPAddress.Any, engineListenPort));
					var dhtEngine = new DhtEngine(dhtListener);
					dhtEngine.Start(dhtNodesData);
					globalEngine.RegisterDht(dhtEngine);

					EngineStartedOnPort(engineListenPort);
				}
				else if (!portsMapped.Contains(engineListenPort) && !portsToMap.Contains(engineListenPort) &&
				         UserSettings.Current.TorrentOptions.EnableUpnp) //we just enabled upnp
				{
					EngineStartedOnPort(engineListenPort);
				}
				else if (!UserSettings.Current.TorrentOptions.EnableUpnp) //we just disabled upnp
				{
					EngineStoppedOnPort(engineListenPort);
				}

				EngineSettings engSets = globalEngine.Settings;
				engSets.GlobalMaxConnections = tOpts.MaxDLConnsNormalized;
				engSets.GlobalMaxDownloadSpeed = tOpts.MaxDLSpeed*1024;
				engSets.GlobalMaxUploadSpeed = tOpts.MaxULSpeed*1024;

				IList<TorrentManager> engineTorrents = globalEngine.Torrents;

				foreach (TorrentManager tm in engineTorrents)
					tm.Settings.UploadSlots = tOpts.NumULSlotsNormalized;
			}
		}

		public static void StopAllTorrents(bool reportToConsole = false)
		{
			if (globalEngine != null)
			{
				var runningTorrents = new List<TorrentManager>();
				if (globalEngine.Torrents != null)
				{
					foreach (TorrentManager tm in globalEngine.Torrents)
					{
						if (tm.State != TorrentState.Stopped)
							runningTorrents.Add(tm);
					}
					globalEngine.StopAll();
				}

				while (runningTorrents.Count > 0)
				{
					int numActiveTorrents = 0;
					foreach (TorrentManager tm in runningTorrents)
					{
						if (!globalEngine.Contains(tm))
							continue;

						try
						{
							if (tm.State != TorrentState.Stopped)
							{
								if (reportToConsole)
									Console.WriteLine("Torrent {0} is in state {1}", tm.InfoHash.ToHex(), tm.State);

								numActiveTorrents++;
							}
						}
						catch (ObjectDisposedException)
						{
						}
					}

					if (numActiveTorrents < 1)
						break;
					Thread.Sleep(250);
				}
			}
		}

		private static void EngineShutdown()
		{
			if (globalEngine != null)
			{
				StopAllTorrents(true);
				EngineStoppedOnPort(engineListenPort);
				engineListenPort = 0;

				string dhtNodesFileName = "";
				try
				{
					dhtNodesFileName = GetDhtNodesFileName();
					File.WriteAllBytes(dhtNodesFileName, globalEngine.DhtEngine.SaveNodes());
				}
				catch (Exception ex)
				{
					Console.WriteLine("Error saving dht nodes file '{0}', reason: {1}", dhtNodesFileName, ex.Message);
				}

				globalEngine.Dispose();
				globalEngine = null;
			}

			foreach (TraceListener lst in Debug.Listeners)
			{
				lst.Flush();
				lst.Close();
			}
		}

		private class AddOnTorrent
		{
			public MetaAddon Meta;
			public string torrentFileName;
			public string torrentSavePath;
		}
	}
}