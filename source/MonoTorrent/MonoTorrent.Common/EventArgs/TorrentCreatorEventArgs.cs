using System;

namespace MonoTorrent.Common
{
	public class TorrentCreatorEventArgs : EventArgs
	{
		#region Member Variables

		private readonly string file;
		private readonly long fileHashed;
		private readonly long fileTotal;
		private readonly long overallHashed;
		private readonly long overallTotal;

		#endregion Member Variables

		#region Properties

		/// <summary>
		///     The number of bytes hashed from the current file
		/// </summary>
		public long FileBytesHashed
		{
			get { return fileHashed; }
		}

		/// <summary>
		///     The size of the current file
		/// </summary>
		public long FileSize
		{
			get { return fileTotal; }
		}

		/// <summary>
		///     The percentage of the current file which has been hashed (range 0-100)
		/// </summary>
		public double FileCompletion
		{
			get { return fileHashed/(double) fileTotal*100.0; }
		}

		/// <summary>
		///     The number of bytes hashed so far
		/// </summary>
		public long OverallBytesHashed
		{
			get { return overallHashed; }
		}

		/// <summary>
		///     The total number of bytes to hash
		/// </summary>
		public long OverallSize
		{
			get { return overallTotal; }
		}

		/// <summary>
		///     The percentage of the data which has been hashed (range 0-100)
		/// </summary>
		public double OverallCompletion
		{
			get { return overallHashed/(double) overallTotal*100.0; }
		}

		/// <summary>
		///     The path of the current file
		/// </summary>
		public string CurrentFile
		{
			get { return file; }
		}

		#endregion Properties

		#region Constructors

		internal TorrentCreatorEventArgs(string file, long fileHashed, long fileTotal, long overallHashed, long overallTotal)
		{
			this.file = file;
			this.fileHashed = fileHashed;
			this.fileTotal = fileTotal;
			this.overallHashed = overallHashed;
			this.overallTotal = overallTotal;
		}

		#endregion Constructors
	}
}