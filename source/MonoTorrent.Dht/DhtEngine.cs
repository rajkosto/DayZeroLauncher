#if !DISABLE_DHT
//
// DhtEngine.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using MonoTorrent.Client;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Listeners;
using MonoTorrent.Dht.Messages;
using MonoTorrent.Dht.Tasks;

namespace MonoTorrent.Dht
{
	internal enum ErrorCode
	{
		GenericError = 201,
		ServerError = 202,
		ProtocolError = 203, // malformed packet, invalid arguments, or bad token
		MethodUnknown = 204 //Method Unknown
	}

	public class DhtEngine : IDisposable, IDhtEngine
	{
		#region Events

		public event EventHandler<PeersFoundEventArgs> PeersFound;
		public event EventHandler StateChanged;

		#endregion Events

		#region Fields

		internal static MainLoop MainLoop = new MainLoop("DhtLoop");
		private readonly MessageLoop messageLoop;
		private readonly RoutingTable table = new RoutingTable();
		private readonly TokenManager tokenManager;
		private readonly Dictionary<NodeId, List<Node>> torrents = new Dictionary<NodeId, List<Node>>();

		private bool bootStrap = true;
		private TimeSpan bucketRefreshTimeout = TimeSpan.FromMinutes(15);
		private bool disposed;
		private DhtState state = DhtState.NotReady;
		private TimeSpan timeout;

		#endregion Fields

		#region Properties

		internal bool Bootstrap
		{
			get { return bootStrap; }
			set { bootStrap = value; }
		}

		internal TimeSpan BucketRefreshTimeout
		{
			get { return bucketRefreshTimeout; }
			set { bucketRefreshTimeout = value; }
		}

		internal NodeId LocalId
		{
			get { return RoutingTable.LocalNode.Id; }
		}

		internal MessageLoop MessageLoop
		{
			get { return messageLoop; }
		}

		internal RoutingTable RoutingTable
		{
			get { return table; }
		}

		internal TimeSpan TimeOut
		{
			get { return timeout; }
			set { timeout = value; }
		}

		internal TokenManager TokenManager
		{
			get { return tokenManager; }
		}

		internal Dictionary<NodeId, List<Node>> Torrents
		{
			get { return torrents; }
		}

		public bool Disposed
		{
			get { return disposed; }
		}

		public DhtState State
		{
			get { return state; }
		}

		#endregion Properties

		#region Constructors

		public DhtEngine(DhtListener listener)
		{
			if (listener == null)
				throw new ArgumentNullException("listener");

			messageLoop = new MessageLoop(this, listener);
			timeout = TimeSpan.FromSeconds(15); // 15 second message timeout by default
			tokenManager = new TokenManager();
		}

		#endregion Constructors

		#region Methods

		public void Add(BEncodedList nodes)
		{
			// Maybe we should pipeline all our tasks to ensure we don't flood the DHT engine.
			// I don't think it's *bad* that we can run several initialise tasks simultaenously
			// but it might be better to run them sequentially instead. We should also
			// run GetPeers and Announce tasks sequentially.
			var task = new InitialiseTask(this, Node.FromCompactNode(nodes));
			task.Execute();
		}

		public void Announce(InfoHash infoHash, int port)
		{
			CheckDisposed();
			Check.InfoHash(infoHash);
			new AnnounceTask(this, infoHash, port).Execute();
		}

		public void GetPeers(InfoHash infoHash)
		{
			CheckDisposed();
			Check.InfoHash(infoHash);
			new GetPeersTask(this, infoHash).Execute();
		}

		public byte[] SaveNodes()
		{
			var details = new BEncodedList();

			MainLoop.QueueWait(delegate
			{
				foreach (Bucket b in RoutingTable.Buckets)
				{
					foreach (Node n in b.Nodes)
						if (n.State != NodeState.Bad)
							details.Add(n.CompactNode());

					if (b.Replacement != null)
						if (b.Replacement.State != NodeState.Bad)
							details.Add(b.Replacement.CompactNode());
				}
			});

			return details.Encode();
		}

		public void Start()
		{
			Start(null);
		}

		public void Start(byte[] initialNodes)
		{
			CheckDisposed();

			messageLoop.Start();
			if (Bootstrap)
			{
				new InitialiseTask(this, initialNodes).Execute();
				RaiseStateChanged(DhtState.Initialising);
				bootStrap = false;
			}
			else
			{
				RaiseStateChanged(DhtState.Ready);
			}

			MainLoop.QueueTimeout(TimeSpan.FromSeconds(1), delegate
			{
				if (Disposed)
					return false;

				foreach (Bucket b in RoutingTable.Buckets)
				{
					if ((DateTime.UtcNow - b.LastChanged) > BucketRefreshTimeout)
					{
						b.LastChanged = DateTime.UtcNow;
						var task = new RefreshBucketTask(this, b);
						task.Execute();
					}
				}
				return !Disposed;
			});
		}

		public void Stop()
		{
			messageLoop.Stop();
		}

		public void Dispose()
		{
			if (disposed)
				return;

			// Ensure we don't break any threads actively running right now
			MainLoop.QueueWait(delegate { disposed = true; });
		}

		internal void Add(IEnumerable<Node> nodes)
		{
			if (nodes == null)
				throw new ArgumentNullException("nodes");

			foreach (Node n in nodes)
				Add(n);
		}

		internal void Add(Node node)
		{
			if (node == null)
				throw new ArgumentNullException("node");

			var task = new SendQueryTask(this, new Ping(RoutingTable.LocalNode.Id), node);
			task.Execute();
		}

		private void CheckDisposed()
		{
			if (Disposed)
				throw new ObjectDisposedException(GetType().Name);
		}

		internal void RaiseStateChanged(DhtState newState)
		{
			state = newState;

			if (StateChanged != null)
				StateChanged(this, EventArgs.Empty);
		}

		internal void RaisePeersFound(NodeId infoHash, List<Peer> peers)
		{
			if (PeersFound != null)
				PeersFound(this, new PeersFoundEventArgs(new InfoHash(infoHash.Bytes), peers));
		}

		#endregion Methods
	}
}

#endif