﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RCONServerPlus
{
	public sealed class RCONClient : IDisposable
	{
		public bool IsConnected
        {
			get
			{
                return tcp.Connected;
            }
        }
        public bool IsInit
        {
			get
			{
				return isInit;
			}
        }
        public bool IsConfigured
		{
			get
			{
                return isConfigured;
            }
        }
		public ClientConfiguration Config
		{
			get
			{
                return config;
            }
        }

        private ClientConfiguration config;
		private string server;
		private string password;
		private int port;
        private int curReconAttempts = 0;
		private static readonly byte[] PADDING = new byte[] { 0x0, 0x0 };
		private bool isInit = false;
		private bool isConfigured = false;
		private NetworkStream stream = null;
		private TcpClient tcp = null;
		private BinaryWriter writer = null;
		private BinaryReader reader = null;
		private ReaderWriterLockSlim threadLock;
		private RCONReader rconReader;

        public RCONClient()
		{
			threadLock = new ReaderWriterLockSlim();
            isInit = false;
			isConfigured = false;
		}

        /// <summary>
        /// Avoid trying to connect to the same server from multiple clients as it could result in an AutshException.
        /// </summary>
        /// <param name="address">IP address of the server</param>
        /// <param name="password">The password configured in server.properties</param>
        /// <returns>RCON Client</returns>
        public RCONClient SetupStream(string address, string password, ClientConfiguration clientConfig = null)
        {
            try
            {
				address = address.Trim();
                if (address == string.Empty)
                {
                    throw new Exception(
                    "Address was invalid.");
                }

                string[] addressComponents = address.Split(':');
                int port = addressComponents.Length > 1 ? int.Parse(addressComponents[1]) : 25565; //Prevents out of bounds exception and will default to 25565
				return SetupStream(addressComponents[0], port, password, clientConfig);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Avoid trying to connect to the same server from multiple clients as it could result in an AutshException.
        /// </summary>
        /// <param name="server">IP of the server</param>
        /// <param name="port">RCON port (defaults to 25565 for minecraft)</param>
        /// <param name="password">The password configured in server.properties</param>
        /// <returns>RCON Client</returns>
		public RCONClient SetupStream(string server, int port, string password, ClientConfiguration clientConfig = null)
        {
            threadLock.EnterWriteLock();

            try
            {
                if (isConfigured)
                {
                    return this;
                }

                this.server = server;
                this.port = port;
                this.password = password;
				if(clientConfig == null)
				{
                    config = ClientConfiguration.DEFAULT;
				}
				else
				{
					config = clientConfig;
				}
                rconReader = new RCONReader();
                isConfigured = true;
                OpenConnection();
                return this;
            }
            finally
            {
                threadLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Sends a command to the server and returns its response (Not all commands will give a response)
        /// </summary>
        /// <param name="command">Minecraft command with '/' removed</param>
        /// <returns>Servers response</returns>
        public string SendCommand(string command)
		{
			if (!isConfigured)
			{
				return RCONMessageAnswer.EMPTY.Answer;
			}

			return InternalSendMessage(RCONMessageType.Command, command).Answer;
		}

        /// <summary>
		/// Sends a message to the server and returns its response (Not all messages will give a response)
		/// </summary>
		/// <param name="type"></param>
		/// <param name="message"></param>
		/// <returns>Servers response</returns>
        public string SendMessage(RCONMessageType type, string message)
		{
			if (!isConfigured)
			{
				return RCONMessageAnswer.EMPTY.Answer;
			}

			return InternalSendMessage(type, message).Answer;
		}

        /// <summary>
        /// Sends a message to the server but does not wait for a response
        /// </summary>
        /// <param name="type"></param>
        /// <param name="command"></param>
        public void FireAndForgetMessage(RCONMessageType type, string command)
		{
			if (!isConfigured)
			{
				return;
			}

			InternalSendMessage(type, command, true);
		}

		private void OpenConnection()
		{
			if (isInit)
			{
				return;
			}

			try
			{
				tcp = new TcpClient(server, port);
				stream = tcp.GetStream();
				writer = new BinaryWriter(stream);
				reader = new BinaryReader(stream);
				rconReader.Setup(reader);

				if (password != string.Empty)
				{
					var answer = InternalSendAuth();

					//Response ID of -1 means auth failed aka and EMPTY message apparently
					if (answer == RCONMessageAnswer.EMPTY)
					{
						isInit = false;
						throw new AuthException("Authentication failed (check password)");
					}
				}

				isInit = true;
			}
			catch(AuthException ex)
			{
                isInit = false;
                isConfigured = false;
                Console.Error.WriteLine("Exception while connecting: " + ex.Message);

				//Only say this if there are reconnection attempts remaining
				if(curReconAttempts != config.reconnectAttempts)
				{
					Console.Error.WriteLine("Reconnection will not be attempted due to this error");
				}
            }
			catch(Exception ex)
			{
                isInit = false;
                isConfigured = false;
                Console.Error.WriteLine("Exception while connecting: " + ex.Message);
				if (config.retryConnect)
				{
                    if (curReconAttempts < config.reconnectAttempts)
                    {
                        curReconAttempts++;
                        Console.Error.WriteLine($"Attempting reconnect in {config.reconnectDelaySeconds} seconds [{curReconAttempts}/{config.reconnectAttempts}]");
                        Thread.Sleep(TimeSpan.FromSeconds(config.reconnectDelaySeconds));
                        OpenConnection();
                    }
                    else
                    {
                        curReconAttempts = 0;
                    }
                }
            }
            finally
            {
                // To prevent huge CPU load if many reconnects happens.
                // Does not effect any normal case ;-)
                Thread.Sleep(TimeSpan.FromSeconds(0.1));
            }
        }
		private RCONMessageAnswer InternalSendAuth()
		{
			// Build the message:
			var command = password;
			var type = RCONMessageType.Login;
			var messageNumber = ThreadSafeIncrement.Get();
			var msg = new List<byte>();
			msg.AddRange(BitConverter.GetBytes(10 + Encoding.UTF8.GetByteCount(command)));
            msg.AddRange(BitConverter.GetBytes(messageNumber));
			msg.AddRange(BitConverter.GetBytes((int)type));
			msg.AddRange(Encoding.UTF8.GetBytes(command));
			msg.AddRange(PADDING);

			// Write the message to the wire:
			writer.Write(msg.ToArray());
			writer.Flush();
            //Console.WriteLine($"Auth Sent [{messageNumber}]");

            return WaitReadMessage(messageNumber);
		}
		private RCONMessageAnswer InternalSendMessage(RCONMessageType type, string command, bool fireAndForget = false)
		{
			try
			{
				var messageNumber = 0;

				try
				{
					threadLock.EnterWriteLock();

					// Is a reconnection necessary?
					if (!isInit || tcp == null || !tcp.Connected)
					{
						InternalDispose();
						OpenConnection();
					}

                    // Build the message:
                    messageNumber = ThreadSafeIncrement.Get();
					var msg = new List<byte>();
					msg.AddRange(BitConverter.GetBytes(10 + Encoding.UTF8.GetByteCount(command)));
					msg.AddRange(BitConverter.GetBytes(messageNumber));
					msg.AddRange(BitConverter.GetBytes((int)type));
					msg.AddRange(Encoding.UTF8.GetBytes(command));
					msg.AddRange(PADDING);

					// Write the message to the wire:
					writer.Write(msg.ToArray());
					writer.Flush();

					Console.WriteLine($"Message Sent [{messageNumber}]: {command}");
				}
				finally
				{
					threadLock.ExitWriteLock();
				}

				if (fireAndForget && config.rconServerIsMultiThreaded)
				{
					var id = messageNumber;
					Task.Factory.StartNew(() =>
					{
						WaitReadMessage(id);
					});

					return RCONMessageAnswer.EMPTY;
				}

				return WaitReadMessage(messageNumber);
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while sending: " + e.Message);
				return RCONMessageAnswer.EMPTY;
			}
		}
		private RCONMessageAnswer WaitReadMessage(int messageNo)
		{
			var sendTime = DateTime.UtcNow;
			while (true)
			{
				var answer = rconReader.GetAnswer(messageNo);
				if (answer == RCONMessageAnswer.EMPTY)
				{
					//If timeoutSeconds is negative keep trying
					if (config.timeoutSeconds > 0 && (DateTime.UtcNow - sendTime).TotalSeconds > config.timeoutSeconds)
					{
						return RCONMessageAnswer.EMPTY;
					}

					Thread.Sleep(TimeSpan.FromSeconds(0.001));
					continue;
				}

				return answer;
			}
		}

		#region IDisposable implementation
		public void Dispose()
		{
			threadLock.EnterWriteLock();

			try
			{
				InternalDispose();
			}
			finally
			{
				threadLock.ExitWriteLock();
			}
		}
        private void InternalDispose()
        {
            isInit = false;

            try
            {
                rconReader.Dispose();
            }
            catch
            {
            }

            if (writer != null)
            {
                try
                {
                    writer.Dispose();
                }
                catch
                {
                }
            }

            if (reader != null)
            {
                try
                {
                    reader.Dispose();
                }
                catch
                {
                }
            }

            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                }
            }

            if (tcp != null)
            {
                try
                {
                    tcp.Close();
                }
                catch
                {
                }
            }
        }
        #endregion
    }
}
