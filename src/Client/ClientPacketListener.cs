﻿using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Timers;
using Hermes.Diagnostics;
using Hermes.Flows;
using Hermes.Packets;
using Hermes.Properties;

namespace Hermes
{
	public class ClientPacketListener : IPacketListener
	{
		private static readonly ITracer tracer = Tracer.Get<ClientPacketListener> ();

		IDisposable firstPacketSubscription;
		IDisposable nextPacketsSubscription;
		IDisposable allPacketsSubscription;
		IDisposable senderSubscription;

		readonly IProtocolFlowProvider flowProvider;
		readonly ProtocolConfiguration configuration;
		readonly ReplaySubject<IPacket> packets;
		Timer keepAliveTimer;
		bool disposed;

		public ClientPacketListener (IProtocolFlowProvider flowProvider, ProtocolConfiguration configuration)
		{
			this.flowProvider = flowProvider;
			this.configuration = configuration;
			this.packets = new ReplaySubject<IPacket> (window: TimeSpan.FromSeconds(configuration.WaitingTimeoutSecs));
		}

		public IObservable<IPacket> Packets { get { return this.packets; } }

		public void Listen (IChannel<IPacket> channel)
		{
			if (this.disposed) {
				throw new ObjectDisposedException (this.GetType ().FullName);
			}

			var clientId = string.Empty;

			this.firstPacketSubscription = channel.Receiver
				.FirstOrDefaultAsync()
				.Subscribe(async packet => {
					if (packet == default (IPacket)) {
						return;
					}

					tracer.Info (Resources.Tracer_ClientPacketListener_FirstPacketReceived, clientId, packet.Type);

					var connectAck = packet as ConnectAck;

					if (connectAck == null) {
						this.NotifyError (Resources.ClientPacketListener_FirstReceivedPacketMustBeConnectAck);
						return;
					}

					await this.DispatchPacketAsync (packet, clientId, channel);
				}, ex => {
					this.NotifyError (ex);
				});

			this.nextPacketsSubscription = channel.Receiver
				.Skip(1)
				.Subscribe (async packet => {
					await this.DispatchPacketAsync (packet, clientId, channel);
				}, ex => {
					this.NotifyError (ex);
				});

			this.allPacketsSubscription = channel.Receiver.Subscribe (_ => { }, () => {
				tracer.Warn (Resources.Tracer_PacketChannelCompleted, clientId);

				this.packets.OnCompleted ();	
			});

			this.senderSubscription = channel.Sender
				.OfType<Connect> ()
				.FirstAsync ()
				.Subscribe (connect => {
					clientId = connect.ClientId;

					if (this.configuration.KeepAliveSecs > 0) {
						this.StartKeepAliveMonitor (channel, clientId);
					}
				});
		}

		public void Dispose ()
		{
			this.Dispose (disposing: true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (this.disposed) {
				return;
			}

			if (disposing) {
				this.firstPacketSubscription.Dispose ();
				this.nextPacketsSubscription.Dispose ();
				this.allPacketsSubscription.Dispose ();
				this.senderSubscription.Dispose ();

				if (this.keepAliveTimer != null) {
					this.keepAliveTimer.Dispose ();
				}
				
				this.disposed = true;
			}
		}

		private void StartKeepAliveMonitor(IChannel<IPacket> channel, string clientId)
		{
			var interval = this.configuration.KeepAliveSecs * 1000;

			this.keepAliveTimer = new Timer();

			this.keepAliveTimer.AutoReset = true;
			this.keepAliveTimer.Interval = interval;
			this.keepAliveTimer.Elapsed += async (sender, e) => {
				tracer.Warn (Resources.Tracer_ClientPacketListener_SendingKeepAlive, clientId, this.configuration.KeepAliveSecs);

				var ping = new PingRequest ();

				await channel.SendAsync (ping);
			};
			this.keepAliveTimer.Start ();

			channel.Sender.Subscribe (p => {
				this.keepAliveTimer.Interval = interval;
			});
		}

		private async Task DispatchPacketAsync(IPacket packet, string clientId, IChannel<IPacket> channel)
		{
			var flow = this.flowProvider.GetFlow (packet.Type);

			if (flow != null) {
				try {
					tracer.Info (Resources.Tracer_ClientPacketListener_DispatchingMessage, clientId, packet.Type, flow.GetType().Name);

					this.packets.OnNext (packet);

					await flow.ExecuteAsync (clientId, packet, channel);
				} catch (Exception ex) {
					this.NotifyError (ex);
				}
			}
		}

		private void NotifyError(Exception exception)
		{
			this.packets.OnError (exception);
		}

		private void NotifyError(string message)
		{
			this.NotifyError (new ProtocolException (message));
		}

		private void NotifyError(string message, Exception exception)
		{
			this.NotifyError (new ProtocolException (message, exception));
		}
	}
}
