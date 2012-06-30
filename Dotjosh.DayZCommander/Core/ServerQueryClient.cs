﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Dotjosh.DayZCommander.Core
{
	public class ServerQueryClient
	{
		private readonly Server _server;
		private readonly string _ipAddress;
		private readonly int _port;
		private IPEndPoint _ipEndPoint;

		public ServerQueryClient(Server server, string ipAddress, int port)
		{
			_server = server;
			_ipAddress = ipAddress;
			_port = port;
			_ipEndPoint = new IPEndPoint(IPAddress.Any, 0);
		}

		public ServerQueryResult Execute()
		{
			using(var client = new UdpClient(_ipEndPoint))
			{
				client.Client.ReceiveTimeout = 1000 * 10;
				client.Client.SendTimeout = 1000 * 10;

				var s = new Stopwatch();
				s.Start();
				client.Connect(_ipAddress, _port);
				var challengePacket = new byte[] {0xFE, 0xFD, 0x09};
				var basePacket = new byte[] {0xFE, 0xFD, 0x00};
				var idPacket = new byte[] {0x04, 0x05, 0x06, 0x07};
				var fullInfoPacket = new byte[] {0xFF, 0xFF, 0xFF, 0x01};

				var firstRequestPacket = challengePacket.Concat(idPacket).ToArray();
				client.Send(firstRequestPacket, firstRequestPacket.Length);

				var challengeResponse = client.Receive(ref _ipEndPoint);
				s.Stop();

				challengePacket = challengeResponse.Skip(5).ToArray();
				var challengeString = System.Text.Encoding.ASCII.GetString(challengePacket);

				challengePacket = BitConverter.GetBytes(Convert.ToInt32(challengeString)).Reverse().ToArray();
				var secondPacket = basePacket.Concat(idPacket).Concat(challengePacket).Concat(fullInfoPacket).ToArray();
				client.Send(secondPacket, secondPacket.Length);
				var reply = client.Receive(ref _ipEndPoint);
				var response = System.Text.Encoding.ASCII.GetString(reply.Skip(16).ToArray());
				var items = response.Split(new[] {"\0"}, StringSplitOptions.None);

				var settings = new SortedDictionary<string, string>();
				for(int index = 0; index < items.Length; index++)
				{
					if(index == 60)
						break;
					var name = items[index];
					var value = items[index + 1];

					settings.Add(name, value);
					index++;
				}

				var players = items
					.Skip(61)
					.TakeWhile(x => x != "team_" && x != "")
					.Select(x => new Player(_server) {Name = x})
					.ToList();

				var scores = items.SkipWhile(x => x != "score_")
					.Skip(2)
					.Take(players.Count)
					.ToList();


				var deaths = items.SkipWhile(x => x != "deaths_")
					.Skip(2)
					.Take(players.Count)
					.ToList();


				for(int index = 0; index < players.Count; index++)
				{
					var player = players[index];
					player.Score = scores.Count > index
					               	? scores.ElementAt(index).TryInt()
					               	: 0;
					player.Deaths = deaths.Count > index
					                	? deaths.ElementAt(index).TryInt()
					                	: 0;
				}

				return new ServerQueryResult()
				{
					Settings = settings,
					Players = players,
					Ping = s.ElapsedMilliseconds
				};
			}
		}
	}
}