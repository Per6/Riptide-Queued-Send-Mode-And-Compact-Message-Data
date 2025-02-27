﻿// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Transports;
using Riptide.Utils;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Riptide
{
    /// <summary>The send mode of a <see cref="Message"/>.</summary>
    public enum MessageSendMode : byte
    {
        /// <summary>Guarantees order but not delivery. Notifies the sender of what happened via the <see cref="Connection.NotifyDelivered"/> and <see cref="Connection.NotifyLost"/>
        /// events. The receiver must handle notify messages via the <see cref="Connection.NotifyReceived"/> event, <i>which is different from the other two send modes</i>.</summary>
        Notify = MessageHeader.Notify,
        /// <summary>Guarantees neither delivery nor order.</summary>
        Unreliable = MessageHeader.Unreliable,
        /// <summary>Guarantees delivery but not order.</summary>
        Reliable = MessageHeader.Reliable,
		/// <summary>Guarantees both delivery and order. <para>If the queued Messages are backing up, increase <see cref="Connection.MaxSynchronousQueuedMessages"/>.</para></summary>
		Queued = MessageHeader.Queued
    }

    /// <summary>Provides functionality for converting data to bytes and vice versa.</summary>
    public class Message
    {
        /// <summary>The maximum number of bits required for a message's header.</summary>
        public const int MaxHeaderSize = NotifyHeaderBits;
        /// <summary>The number of bits used by the <see cref="MessageHeader"/>.</summary>
        internal const int HeaderBits = 4;
        /// <summary>A bitmask that, when applied, only keeps the bits corresponding to the <see cref="MessageHeader"/> value.</summary>
        internal const byte HeaderBitmask = (1 << HeaderBits) - 1;
        /// <summary>The header size for unreliable messages. Does not count the 2 bytes used for the message ID.</summary>
        /// <remarks>4 bits - header.</remarks>
        internal const int UnreliableHeaderBits = HeaderBits;
        /// <summary>The header size for reliable messages. Does not count the 2 bytes used for the message ID.</summary>
        /// <remarks>4 bits - header, 16 bits - sequence ID.</remarks>
        internal const int ReliableHeaderBits = HeaderBits + 2 * BitsPerByte;
		/// <summary>The header size for queued messages. Does not count the 2 bytes used for the message ID.</summary>
        /// <remarks>4 bits - header, 16 bits - sequence ID.</remarks>
		internal const int QueuedHeaderBits = HeaderBits + 2 * BitsPerByte;
        /// <summary>The header size for notify messages.</summary>
        /// <remarks>4 bits - header, 24 bits - ack, 16 bits - sequence ID.</remarks>
        internal const int NotifyHeaderBits = HeaderBits + 5 * BitsPerByte;
        /// <summary>The minimum number of bytes contained in an unreliable message.</summary>
        internal const int MinUnreliableBytes = UnreliableHeaderBits / BitsPerByte + (UnreliableHeaderBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>The minimum number of bytes contained in a reliable message.</summary>
        internal const int MinReliableBytes = ReliableHeaderBits / BitsPerByte + (ReliableHeaderBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>The minimum number of bytes contained in a notify message.</summary>
        internal const int MinNotifyBytes = NotifyHeaderBits / BitsPerByte + (NotifyHeaderBits % BitsPerByte == 0 ? 0 : 1);
        /// <summary>The number of bits in a byte.</summary>
        private const int BitsPerByte = Converter.BitsPerByte;

		/// <summary>The amount of bytes that can be stored without expanding the Array.</summary>
		public static int InitialMessageSize = 64;
        /// <summary>The maximum number of bytes that a message can contain, including the <see cref="MaxHeaderSize"/>.</summary>
        public static int MaxSize { get; private set; }
        /// <summary>The maximum number of bytes of payload data that a message can contain. This value represents how many bytes can be added to a message <i>on top of</i> the <see cref="MaxHeaderSize"/>.</summary>
        public static int MaxPayloadSize
        {
            get => MaxSize - (MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1));
            set
            {
                if (Peer.ActiveCount > 0)
                    throw new InvalidOperationException($"Changing the '{nameof(MaxPayloadSize)}' is not allowed while a {nameof(Server)} or {nameof(Client)} is running!");

                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), $"'{nameof(MaxPayloadSize)}' cannot be negative!");

                MaxSize = MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1) + value;
                Peer.ByteBuffer = new byte[MaxSize];
                PendingMessage.ClearPool();
            }
        }

        static Message()
        {
            MaxSize = MaxHeaderSize / BitsPerByte + (MaxHeaderSize % BitsPerByte == 0 ? 0 : 1) + 1225;
            Peer.ByteBuffer = new byte[MaxSize];
        }

		/// <summary>The maximum value of Id.</summary>
		public static ushort MaxId { private get; set; } = ushort.MaxValue;
		/// <summary>The sequence Id of the message. <para>Only Queued and reliable have one.</para></summary>
		internal ushort SequenceId {
			get => (ushort)(Data[0] >> HeaderBits);
			set {
				Data[0] &= ~((ulong)ushort.MaxValue << HeaderBits);
				Data[0] |= ((ulong)value) << HeaderBits;
			}
		}
		/// <summary>The Id of a message. <para>Notify doesn't have one.</para></summary>
		public ushort? Id => SendHeader.id;
		/// <summary>The Header of the message.</summary>
		public MessageHeader Header => SendHeader.header;
        /// <summary>How many of this message's bytes are in use.
		/// Rounds up to the next byte because only whole bytes can be sent.</summary>
        public int BytesInUse => data.GetBytesInUse();
        /// <inheritdoc cref="data"/>
        internal ulong[] Data => data.GetData();

        /// <summary>The message's data.</summary>
        private FastBigInt data;
		/// <summary>The mult for new values.</summary>
		private FastBigInt writeValue;
		/// <summary>The header necessary for sending the message.</summary>
		internal (MessageHeader header, ushort? id) SendHeader { get; private set; }
		/// <summary>The current read bit of the message.</summary>
		private int readBit = 0;

        /// <summary>Initializes a <see cref="Message"/> instance.</summary>
		private Message() {}

        /// <summary>Initializes a <see cref="Message"/> instance.</summary>
        private Message(MessageHeader header, ushort? id) {
			if(InitialMessageSize < sizeof(ulong)) throw new InvalidOperationException($"'{nameof(InitialMessageSize)}' must be at least {sizeof(ulong)}!");
			data = new FastBigInt(InitialMessageSize / sizeof(ulong));
			writeValue = new FastBigInt(InitialMessageSize / sizeof(ulong), 1);
			if(id.HasValue) {
				if(id.Value > MaxId) throw new ArgumentOutOfRangeException(nameof(id), $"'{nameof(id)}' cannot be greater than {MaxId}!");
				if(header == MessageHeader.Notify) throw new ArgumentException($"'{nameof(id)}' should not be set for notify messages!", nameof(id));
			} else if(header == MessageHeader.Unreliable
				|| header == MessageHeader.Reliable || header == MessageHeader.Queued) throw new ArgumentException($"'{nameof(id)}' must be set for unreliable, reliable and queued messages!", nameof(id));
			SendHeader = (header, id);
		}

		/// <summary>Initializes a <see cref="Message"/> instance based on
		/// the data recieved.</summary>
		internal Message(byte[] bytes, int contentLength, out ulong info, out MessageSendMode sendMode) {
			data = new FastBigInt(contentLength, bytes);
            writeValue = new FastBigInt(1, 1);
			GetBits(out byte bitfield, HeaderBits);
			MessageHeader header = (MessageHeader)bitfield;
			sendMode = GetMessageSendMode(header);
			switch(sendMode) {
				case MessageSendMode.Notify: GetBits(out info, NotifyHeaderBits - HeaderBits); break;
				case MessageSendMode.Unreliable: GetBits(out info, UnreliableHeaderBits - HeaderBits); break;
				case MessageSendMode.Reliable: GetBits(out info, ReliableHeaderBits - HeaderBits); break;
				case MessageSendMode.Queued: GetBits(out info, QueuedHeaderBits - HeaderBits); break;
				default: throw new ArgumentOutOfRangeException();
			}
			ushort? id = null;
			if(header == MessageHeader.Unreliable || header == MessageHeader.Reliable
				|| header == MessageHeader.Queued) id = GetUShort(0, MaxId);
			SendHeader = (header, id);
		}

        /// <summary>Gets a message instance that can be used for sending.</summary>
        /// <param name="sendMode">The mode in which the message should be sent.</param>
		/// <param name="id">The message's ID.</param>
        /// <returns>A message instance ready to be sent.</returns>
        /// <remarks>This method is primarily intended for use with <see cref="MessageSendMode.Notify"/> as notify messages don't have a built-in message ID, and unlike
        /// <see cref="Create(MessageSendMode, Enum)"/>, this overload does not add a message ID to the message.</remarks>
        public static Message Create(MessageSendMode sendMode, ushort? id = null)
        {
            return Create((MessageHeader)sendMode, id);
        }
        /// <inheritdoc cref="Create(MessageSendMode, ushort?)"/>
        /// <remarks>NOTE: <paramref name="id"/> will be cast to a <see cref="ushort"/>. You should ensure that its value never exceeds that of <see cref="ushort.MaxValue"/>, otherwise you'll encounter unexpected behaviour when handling messages.</remarks>
        public static Message Create(MessageSendMode sendMode, Enum id)
        {
            return Create(sendMode, (ushort)(object)id);
        }
        /// <summary>Gets a message instance that can be used for sending.</summary>
        /// <param name="header">The message's header type.</param>
		/// <param name="id">The message's ID.</param>
        /// <returns>A message instance ready to be sent.</returns>
        internal static Message Create(MessageHeader header, ushort? id = null)
        {
			return new Message(header, id);
        }

		/// <summary>Logs info of the message</summary>
		public void LogStuff(string added = "") {
			RiptideLogger.Log(LogType.Info, $"{data}\n{writeValue}\nRead Bit: {readBit}\nSend Header: {SendHeader}\n{added}");
		}

		/// <summary>Copies a message.</summary>
		/// <returns>The copy of the message.</returns>
		public Message Copy() {
            Message message = new Message() {
				data = data.Copy(),
                writeValue = writeValue.Copy(),
				readBit = readBit,
				SendHeader = SendHeader,
            };
			return message;
        }

        #region Functions
		/// <summary>Gets the MessageSendMode from the header.</summary>
		/// <param name="header">The header to attribute.</param>
		/// <returns>The MessageSendMode attributed to the MessageHeader.</returns>
		private static MessageSendMode GetMessageSendMode(MessageHeader header) {
			if(header == MessageHeader.Notify) return MessageSendMode.Notify;
			else if(header == MessageHeader.Queued) return MessageSendMode.Queued;
			else if(header >= MessageHeader.Reliable) return MessageSendMode.Reliable;
			else return MessageSendMode.Unreliable;
		}

		/// <summary>Adds a sendHeader into the message data.</summary>
		internal MessageSendMode SetSendHeader() {
			ResetReadBit();
			(MessageHeader header, ushort? id) = SendHeader;
			MessageSendMode sendMode = GetMessageSendMode(header);
			ulong mult;
			switch(sendMode) {
				case MessageSendMode.Notify: mult = 1 << NotifyHeaderBits; break;
				case MessageSendMode.Queued: mult = 1 << QueuedHeaderBits; break;
				case MessageSendMode.Reliable: mult = 1 << ReliableHeaderBits; break;
				case MessageSendMode.Unreliable: mult = 1 << UnreliableHeaderBits; break;
				default: throw new ArgumentOutOfRangeException(nameof(header), header, null);
			}
			ulong umid = 0;
			if(id != null) {
				if(id.Value > MaxId) throw new ArgumentOutOfRangeException(nameof(id), $"'{nameof(id)}' cannot be greater than {MaxId}!");
				if(sendMode == MessageSendMode.Notify) throw new ArgumentException($"'{nameof(id)}' cannot be set for {nameof(MessageSendMode.Notify)} messages!", nameof(id));
				umid = mult * id.Value;
				mult *= MaxId + 1UL;
			}
			data.Mult(mult);
			Data[0] += (ulong)header;
			Data[0] += umid;
			return sendMode;
		}

		/// <summary>Removes the sendHeader from the message data.</summary>
		/// <remarks>This is necessary when you send a message and want to read or write from/to it afterwards.</remarks>
		internal void RemoveSendHeader() {
			ulong div;
			MessageSendMode sendMode = GetMessageSendMode(SendHeader.header);
			switch(sendMode) {
				case MessageSendMode.Notify: div = 1 << NotifyHeaderBits; break;
				case MessageSendMode.Queued: div = 1 << QueuedHeaderBits; break;
				case MessageSendMode.Reliable: div = 1 << ReliableHeaderBits; break;
				case MessageSendMode.Unreliable: div = 1 << UnreliableHeaderBits; break;
				default: throw new ArgumentOutOfRangeException(nameof(sendMode), sendMode, null);
			}
			if(SendHeader.id != null) div *= MaxId + 1UL;
			data.DivReturnMod(div);
		}

		/// <summary>Divides data by 2^readBit, so the readBit can go back to 0.</summary>
		internal void ResetReadBit() {
			data.RightShiftArbitrary(readBit);
			readBit = 0;
		}

		/// <summary>Creates a QueuedAck message containing sequence ID.</summary>
		/// <param name="sequenceId">The sequence id to queue.</param>
		/// <param name="successfull">Whether or not the sequence was successful or needs to be resent.</param>
		/// <returns>The new message.</returns>
		internal static Message QueuedAck(ushort sequenceId, bool successfull) {
            Message message = Create(MessageHeader.QueuedAck);
			message.AddUShort(sequenceId);
			message.AddBool(successfull);
			return message;
        }
        #endregion

        #region Add & Retrieve Data
        #region Message
		/// <summary>Adds a <see cref="Message"/> to the message.</summary>
		/// <param name="message">The message to add.</param>
		/// <param name="takeSendHeader">Wether to take on the send Header as specified in Create.</param>
		/// <returns>The message that the message was added to.</returns>
		/// <remarks>This method does not move <paramref name="message"/>'s internal read position!</remarks>
		public Message AddMessage(Message message, bool takeSendHeader = false)
		{
			message.ResetReadBit();
			ResetReadBit();
			if(writeValue <= data) throw new ArgumentException("First read all the data of a message before adding new data", nameof(data));
			if(message.writeValue <= message.data) throw new ArgumentException(nameof(message), $"Cannot add a message with unknown write value! (All recieved messages don't know how many possible states have been written to it)\n{message.data}\n{message.writeValue}");
			if(takeSendHeader) SendHeader = message.SendHeader;
			BigInteger data1 = (BigInteger)data;
			BigInteger data2 = (BigInteger)message.data;
			BigInteger write1 = (BigInteger)writeValue;
			BigInteger write2 = (BigInteger)message.writeValue;
			data1 += data2 * write1;
			write1 *= write2;
			data = (FastBigInt)data1;
			writeValue = (FastBigInt)write1;
			return this;
		}

		/// <summary>Adds messages of the same MessageHeader to the message.</summary>
		/// <param name="messages">The messages to add</param>
		/// <returns>This message.</returns>
		public Message AddMessages(params Message[] messages) {
			MessageHeader header = SendHeader.header;
			bool usesId = SendHeader.id != null;
			if(!writeValue.IsPowerOf2()) throw new ArgumentException("Can not add messages to a message with a write value that is not a power of 2");
			AddVarULong((ulong)messages.Length);
			foreach(Message m in messages) {
				(MessageHeader mheader, ushort? id) = m.SendHeader;
				if(mheader != header) throw new ArgumentException("All messages must have the same header!", nameof(messages));
				if((id != null) != usesId) throw new ArgumentException("All messages must have the same id usage!", nameof(messages));
				AddVarULong((ulong)m.writeValue.Log2Ceil());
				if(usesId) AddUShort(id.Value);
				AddMessage(m, false);
				writeValue.RoundToNextPowerOf2();
			}
			return this;
		}

		/// <summary>Retrieves the messages from the message.</summary>
		/// <returns>The retrieved messages.</returns>
		public Message[] GetMessages() {
			MessageHeader header = SendHeader.header;
			bool usesId = SendHeader.id != null;
			int length = (int)GetVarULong();
			Message[] messages = new Message[length];
			for(int i = 0; i < length; i++) {
				if(usesId) messages[i] = Create(header, GetUShort());
				else messages[i] = Create(header);
				int bits = (int)GetVarULong();
				int ulongs = bits / 64;
				for(int j = 0; j < ulongs; j++) messages[i].AddULong(GetULong());
				GetBits(out ulong u, bits % 64);
				messages[i].AddBits(u, bits % 64);
			}
			return messages;
		}

		/// <summary>Sets the send header of the message.</summary>
		/// <param name="header">The header to set.</param>
		/// <param name="id">The id to set.</param>
		/// <returns>The message that the send header was set on.</returns>
		public Message SetSendHeader(MessageHeader header, byte? id) {
			SendHeader = (header, id);
			return this;
		}

		/// <summary>Gets all the bits of a message.</summary>
		/// <returns>The bits of the message.</returns>
		public IEnumerable<bool> GetAllBits() {
			ulong[] uldata = Data;
			int length = data.MaxIndex;
			for(int i = 0; i < length; i++) {
				ulong ul = uldata[i];
				for(int j = 0; j < sizeof(ulong) * BitsPerByte; j++)
					yield return (ul & (1UL << j)) != 0;
			}
		}
        #endregion

        #region Bits
		/// <summary>Sets bits to the message.</summary>
		/// <param name="bitfield">The bitfield to add.</param>
		/// <param name="amount">The amount of bits.</param>
		/// <param name="size">The size of the bitfield.</param>
		/// <returns>The message that the bits were added to.</returns>
		private Message AddBits(ulong bitfield, int amount, int size) {
			if(amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), $"'{nameof(amount)}' cannot be negative!");
			if(amount > size * BitsPerByte) throw new ArgumentOutOfRangeException(nameof(amount), $"Cannot add more than {size * BitsPerByte} bits at a time!");
			ulong mask = CMath.GetMask(amount);
			return AddULong(bitfield, 0, mask);
		}
		/// <inheritdoc cref="AddBits(ulong, int, int)"/>
		public Message AddBits(byte bitfield, int amount) => AddBits(bitfield, amount, sizeof(byte));
		/// <inheritdoc cref="AddBits(ulong, int, int)"/>
		public Message AddBits(ushort bitfield, int amount) => AddBits(bitfield, amount, sizeof(ushort));
		/// <inheritdoc cref="AddBits(ulong, int, int)"/>
		public Message AddBits(uint bitfield, int amount) => AddBits(bitfield, amount, sizeof(uint));
		/// <inheritdoc cref="AddBits(ulong, int, int)"/>
		public Message AddBits(ulong bitfield, int amount) => AddBits(bitfield, amount, sizeof(ulong));
		/// <summary>Gets bits from a message.</summary>
		/// <param name="bitfield">The bitfield to get.</param>
		/// <param name="amount">The amount of bits.</param>
		/// <param name="size">The size of the bitfield.</param>
		private void GetBits(out ulong bitfield, int amount, int size) {
			if(amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), $"'{nameof(amount)}' cannot be negative!");
			if(amount > size * BitsPerByte) throw new ArgumentOutOfRangeException(nameof(amount), $"Cannot add more than {size * BitsPerByte} bits at a time!");
			ulong mask = CMath.GetMask(amount);
			bitfield = GetULong(0, mask);
		}
		/// <inheritdoc cref="GetBits(out ulong, int, int)"/>
		public void GetBits(out byte bitfield, int amount) { GetBits(out ulong temp, amount, sizeof(byte)); bitfield = (byte)temp; }
		/// <inheritdoc cref="GetBits(out ulong, int, int)"/>
		public void GetBits(out ushort bitfield, int amount) { GetBits(out ulong temp, amount, sizeof(ushort)); bitfield = (ushort)temp; }
		/// <inheritdoc cref="GetBits(out ulong, int, int)"/>
		public void GetBits(out uint bitfield, int amount) { GetBits(out ulong temp, amount, sizeof(uint)); bitfield = (uint)temp; }
		/// <inheritdoc cref="GetBits(out ulong, int, int)"/>
		public void GetBits(out ulong bitfield, int amount) { GetBits(out bitfield, amount, sizeof(ulong)); }
        #endregion

        #region Varint
        /// <summary>Adds a positive or negative number to the message, using fewer bits for smaller values.</summary>
        /// <inheritdoc cref="AddVarULong(ulong)"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message AddVarLong(long value) => AddVarULong((ulong)Converter.ZigZagEncode(value));
        /// <summary>Adds a positive number to the message, using fewer bits for smaller values.</summary>
        /// <param name="value">The value to add.</param>
        /// <returns>The message that the value was added to.</returns>
        /// <remarks>The value is added in segments of 8 bits, 1 of which is used to indicate whether or not another segment follows. As a result, small values are
        /// added to the message using fewer bits, while large values will require a few more bits than they would if they were added via <see cref="AddByte(byte, byte, byte)"/>,
        /// <see cref="AddUShort(ushort, ushort, ushort)"/>, <see cref="AddUInt(uint, uint, uint)"/>, or <see cref="AddULong(ulong, ulong, ulong)"/> (or their signed counterparts).</remarks>
        public Message AddVarULong(ulong value)
        {
            do
            {
                byte byteValue = (byte)(value & 0b_0111_1111);
                value >>= 7;
                if (value != 0) // There's more to write
                    byteValue |= 0b_1000_0000;

                AddByte(byteValue);
            }
            while (value != 0);

            return this;
        }

        /// <summary>Retrieves a positive or negative number from the message, using fewer bits for smaller values.</summary>
        /// <inheritdoc cref="GetVarULong()"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetVarLong() => Converter.ZigZagDecode((long)GetVarULong());
        /// <summary>Retrieves a positive number from the message, using fewer bits for smaller values.</summary>
        /// <returns>The value that was retrieved.</returns>
        /// <remarks>The value is retrieved in segments of 8 bits, 1 of which is used to indicate whether or not another segment follows. As a result, small values are
        /// retrieved from the message using fewer bits, while large values will require a few more bits than they would if they were retrieved via <see cref="GetByte(byte, byte)"/>,
        /// <see cref="GetUShort(ushort, ushort)"/>, <see cref="GetUInt(uint, uint)"/>, or <see cref="GetULong(ulong, ulong)"/> (or their signed counterparts).</remarks>
        public ulong GetVarULong()
        {
            ulong byteValue;
            ulong value = 0;
            int shift = 0;

            do
            {
                byteValue = GetByte();
                value |= (byteValue & 0b_0111_1111) << shift;
                shift += 7;
            }
            while ((byteValue & 0b_1000_0000) != 0);

            return value;
        }
        #endregion

        #region Byte & SByte
		/// <summary>Adds an <see cref="byte"/> to the message.</summary>
        /// <param name="value">The <see cref="byte"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="byte"/> was added to.</returns>
		public Message AddByte(byte value, byte min = byte.MinValue, byte max = byte.MaxValue) {
			return AddULong(value, min, max);
		}

		/// <summary>Retrieves an <see cref="byte"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="byte"/> that was retrieved.</returns>
		public byte GetByte(byte min = byte.MinValue, byte max = byte.MaxValue) {
			return (byte)GetULong(min, max);
		}

		/// <summary>Adds an <see cref="sbyte"/> to the message.</summary>
        /// <param name="value">The <see cref="sbyte"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="sbyte"/> was added to.</returns>
		public Message AddSByte(sbyte value, sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue)
			=> AddByte(value.Conv(), min.Conv(), max.Conv());

		/// <summary>Retrieves an <see cref="sbyte"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="sbyte"/> that was retrieved.</returns>
		public sbyte GetSByte(sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue) {
			return GetByte(min.Conv(), max.Conv()).Conv();
		}

        /// <summary>Adds a <see cref="byte"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddBytes(byte[] array, bool includeLength = true, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

			for (int i = 0; i < array.Length; i++)
				AddByte(array[i], min, max);

            return this;
        }

        /// <summary>Adds a <see cref="byte"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="startIndex">The position at which to start adding from the array.</param>
        /// <param name="amount">The amount of bytes to add from the startIndex of the array.</param>
        /// <param name="includeLength">Whether or not to include the <paramref name="amount"/> in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddBytes(byte[] array, int startIndex, int amount, bool includeLength = true, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            if (startIndex < 0 || startIndex >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            if (startIndex + amount > array.Length)
                throw new ArgumentException($"The source array is not long enough to read {amount} {Helper.CorrectForm(amount, ByteName)} starting at {startIndex}!", nameof(amount));

            if (includeLength)
                AddVarULong((uint)amount);

			for (int i = startIndex; i < startIndex + amount; i++)
			{
				AddByte(array[i], min, max);
			}

            return this;
        }

        /// <summary>Adds an <see cref="sbyte"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddSBytes(sbyte[] array, bool includeLength = true, sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddSByte(array[i], min, max);
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="byte"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public byte[] GetBytes(byte min = byte.MinValue, byte max = byte.MaxValue) => GetBytes((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="byte"/> array from the message.</summary>
        /// <param name="amount">The amount of bytes to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public byte[] GetBytes(int amount, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            byte[] array = new byte[amount];
            ReadBytes(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="byte"/> array with bytes retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetBytes(byte[] intoArray, int startIndex = 0, byte min = byte.MinValue, byte max = byte.MaxValue) => GetBytes((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="byte"/> array with bytes retrieved from the message.</summary>
        /// <param name="amount">The amount of bytes to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetBytes(int amount, byte[] intoArray, int startIndex = 0, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ByteName), nameof(amount));

            ReadBytes(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Retrieves an <see cref="sbyte"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public sbyte[] GetSBytes(sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue) => GetSBytes((int)GetVarULong(), min, max);
        /// <summary>Retrieves an <see cref="sbyte"/> array from the message.</summary>
        /// <param name="amount">The amount of sbytes to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public sbyte[] GetSBytes(int amount, sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue)
        {
            sbyte[] array = new sbyte[amount];
            ReadSBytes(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="sbyte"/> array with bytes retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating <paramref name="intoArray"/>.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetSBytes(sbyte[] intoArray, int startIndex = 0, sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue) => GetSBytes((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="sbyte"/> array with bytes retrieved from the message.</summary>
        /// <param name="amount">The amount of sbytes to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating <paramref name="intoArray"/>.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetSBytes(int amount, sbyte[] intoArray, int startIndex = 0, sbyte min = sbyte.MinValue, sbyte max = sbyte.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, SByteName), nameof(amount));

            ReadSBytes(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Reads a number of bytes from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of bytes to read.</param>
        /// <param name="intoArray">The array to write the bytes into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadBytes(int amount, byte[] intoArray, int startIndex, byte min, byte max)
        {
			for (int i = 0; i < amount; i++)
				intoArray[startIndex + i] = GetByte(min, max);
        }

        /// <summary>Reads a number of sbytes from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of sbytes to read.</param>
        /// <param name="intoArray">The array to write the sbytes into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadSBytes(int amount, sbyte[] intoArray, int startIndex, sbyte min, sbyte max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetSByte(min, max);
            }
        }
        #endregion

        #region Bool
		/// <summary>Adds a <see cref="bool"/> to the message.</summary>
        /// <param name="value">The <see cref="bool"/> to add.</param>
        /// <returns>The message that the <see cref="bool"/> was added to.</returns>
		public Message AddBool(bool value) => AddULong(value.ToULong(), 0, 1);
		/// <summary>Retrieves a <see cref="bool"/> from the message.</summary>
        /// <returns>The <see cref="bool"/> that was retrieved.</returns>
		public bool GetBool() => GetULong(0, 1) == 1;

        /// <summary>Adds a <see cref="bool"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddBools(bool[] array, bool includeLength = true)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
                AddBool(array[i]);

            return this;
        }

        /// <summary>Retrieves a <see cref="bool"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public bool[] GetBools() => GetBools((int)GetVarULong());
        /// <summary>Retrieves a <see cref="bool"/> array from the message.</summary>
        /// <param name="amount">The amount of bools to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public bool[] GetBools(int amount)
        {
            bool[] array = new bool[amount];
            ReadBools(amount, array);
            return array;
        }
        /// <summary>Populates a <see cref="bool"/> array with bools retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetBools(bool[] intoArray, int startIndex = 0) => GetBools((int)GetVarULong(), intoArray, startIndex);
        /// <summary>Populates a <see cref="bool"/> array with bools retrieved from the message.</summary>
        /// <param name="amount">The amount of bools to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetBools(int amount, bool[] intoArray, int startIndex = 0)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, BoolName), nameof(amount));

            ReadBools(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of bools from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of bools to read.</param>
        /// <param name="intoArray">The array to write the bools into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
        private void ReadBools(int amount, bool[] intoArray, int startIndex = 0)
        {
            for (int i = 0; i < amount; i++)
                intoArray[startIndex + i] = GetBool();
        }
        #endregion

		#region Enum
		/// <summary>Adds an Enum to the message.</summary>
		/// <param name="value">The enum to add.</param>
		/// <returns>The message that the <see cref="Enum"/> was added to.</returns>
		public Message AddEnum<T>(T value) where T : Enum {
			T[] possibleValues = (T[])Enum.GetValues(typeof(T));
			return AddElement(value, possibleValues);
		}

		/// <summary>Retrieves an Enum from the message.</summary>
		/// <typeparam name="T">The type of the enum.</typeparam>
		/// <returns>The enum that was retrieved.</returns>
		public T GetEnum<T>() where T : Enum {
			T[] possibleValues = (T[])Enum.GetValues(typeof(T));
			return GetElement(possibleValues);
		}

		/// <summary>Adds a <see cref="Enum"/> array to the message.</summary>
		/// <param name="values">The enum values to add to the message.</param>
		/// <returns>The message that the array was added to.</returns>
		public Message AddEnums(params Enum[] values) {
			if(values == null) throw new ArgumentNullException(nameof(values));
			foreach(Enum value in values)
				AddEnum(value);
			
			return this;
		}

		/// <summary>Retrieves a <see cref="Enum"/> array from the message.</summary>
		/// <param name="types">The types of the enums to retrieve.</param>
		/// <returns>The array that was retrieved.</returns>
		public Enum[] GetEnums(params Type[] types) {
			if(types == null) throw new ArgumentNullException(nameof(types));
			Enum[] array = new Enum[types.Length];
			for(int i = 0; i < types.Length; i++) {
				Type type = types[i];
				if(!type.IsEnum) throw new ArgumentException($"Type {type} is not an enum", nameof(types));
				Enum[] possibleValues = (Enum[])Enum.GetValues(type);
        		array[i] = GetElement(possibleValues);
			}
			return array;
		}

		/// <summary>Retrieves a <see cref="Enum"/> array from the message.</summary>
		/// <typeparam name="T">The type of the enums.</typeparam>
		/// <param name="amount">The amount of enums to get.</param>
		/// <returns>The array that was retrieved.</returns>
		public T[] GetEnums<T>(int amount) where T : Enum {
			T[] possibleValues = (T[])Enum.GetValues(typeof(T));
			return GetElements(amount, possibleValues);
		}
		#endregion

        #region Short & UShort
		/// <summary>Adds a <see cref="short"/> to the message.</summary>
        /// <param name="value">The <see cref="short"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="short"/> was added to.</returns>
		public Message AddUShort(ushort value, ushort min = ushort.MinValue, ushort max = ushort.MaxValue) {
			return AddULong(value, min, max);
		}

		/// <summary>Retrieves a <see cref="ushort"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="ushort"/> that was retrieved.</returns>
		public ushort GetUShort(ushort min = ushort.MinValue, ushort max = ushort.MaxValue) {
			return (ushort)GetULong(min, max);
		}

		/// <summary>Adds a <see cref="short"/> to the message.</summary>
        /// <param name="value">The <see cref="short"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="short"/> was added to.</returns>
		public Message AddShort(short value, short min = short.MinValue, short max = short.MaxValue)
			=> AddUShort(value.Conv(), min.Conv(), max.Conv());

		/// <summary>Retrieves a <see cref="short"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="short"/> that was retrieved.</returns>
		public short GetShort(short min = short.MinValue, short max = short.MaxValue) {
			return GetUShort(min.Conv(), max.Conv()).Conv();
		}

        /// <summary>Adds a <see cref="short"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddShorts(short[] array, bool includeLength = true, short min = short.MinValue, short max = short.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddShort(array[i], min, max);
            }

            return this;
        }

        /// <summary>Adds a <see cref="ushort"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddUShorts(ushort[] array, bool includeLength = true, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddUShort(array[i], min, max);
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="short"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public short[] GetShorts(short min = short.MinValue, short max = short.MaxValue) => GetShorts((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="short"/> array from the message.</summary>
        /// <param name="amount">The amount of shorts to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public short[] GetShorts(int amount, short min = short.MinValue, short max = short.MaxValue)
        {
            short[] array = new short[amount];
            ReadShorts(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="short"/> array with shorts retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetShorts(short[] intoArray, int startIndex = 0, short min = short.MinValue, short max = short.MaxValue) => GetShorts((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="short"/> array with shorts retrieved from the message.</summary>
        /// <param name="amount">The amount of shorts to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetShorts(int amount, short[] intoArray, int startIndex = 0, short min = short.MinValue, short max = short.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ShortName), nameof(amount));

            ReadShorts(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Retrieves a <see cref="ushort"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public ushort[] GetUShorts(ushort min = ushort.MinValue, ushort max = ushort.MaxValue) => GetUShorts((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="ushort"/> array from the message.</summary>
        /// <param name="amount">The amount of ushorts to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public ushort[] GetUShorts(int amount, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            ushort[] array = new ushort[amount];
            ReadUShorts(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="ushort"/> array with ushorts retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetUShorts(ushort[] intoArray, int startIndex = 0, ushort min = ushort.MinValue, ushort max = ushort.MaxValue) => GetUShorts((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="ushort"/> array with ushorts retrieved from the message.</summary>
        /// <param name="amount">The amount of ushorts to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetUShorts(int amount, ushort[] intoArray, int startIndex = 0, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, UShortName), nameof(amount));

            ReadUShorts(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Reads a number of shorts from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of shorts to read.</param>
        /// <param name="intoArray">The array to write the shorts into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadShorts(int amount, short[] intoArray, int startIndex, short min, short max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetShort(min, max);
            }
        }

        /// <summary>Reads a number of ushorts from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ushorts to read.</param>
        /// <param name="intoArray">The array to write the ushorts into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadUShorts(int amount, ushort[] intoArray, int startIndex, ushort min, ushort max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetUShort(min, max);
            }
        }
        #endregion

        #region Int & UInt
		/// <summary>Adds a <see cref="uint"/> to the message.</summary>
        /// <param name="value">The <see cref="uint"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="uint"/> was added to.</returns>
		public Message AddUInt(uint value, uint min = uint.MinValue, uint max = uint.MaxValue) {
			return AddULong(value, min, max);
		}

		/// <summary>Retrieves an <see cref="uint"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <returns>The <see cref="uint"/> that was retrieved.</returns>
		public uint GetUInt(uint min = uint.MinValue, uint max = uint.MaxValue) {
			return (uint)GetULong(min, max);
		}

		/// <summary>Adds an <see cref="int"/> to the message.</summary>
		/// <param name="value">The <see cref="int"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="int"/> was added to.</returns>
		public Message AddInt(int value, int min = int.MinValue, int max = int.MaxValue)
			=> AddUInt(value.Conv(), min.Conv(), max.Conv());

		/// <summary>Retrieves an <see cref="int"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <returns>The <see cref="int"/> that was retrieved.</returns>
		public int GetInt(int min = int.MinValue, int max = int.MaxValue) {
			return GetUInt(min.Conv(), max.Conv()).Conv();
		}

        /// <summary>Adds an <see cref="int"/> array message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddInts(int[] array, bool includeLength = true, int min = int.MinValue, int max = int.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddInt(array[i], min, max);
            }

            return this;
        }

        /// <summary>Adds a <see cref="uint"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddUInts(uint[] array, bool includeLength = true, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddUInt(array[i], min, max);
            }

            return this;
        }

        /// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public int[] GetInts() => GetInts((int)GetVarULong(), int.MinValue, int.MaxValue);
        /// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public int[] GetInts(int amount)
        {
            int[] array = new int[amount];
            ReadInts(amount, array, 0, int.MinValue, int.MaxValue);
            return array;
        }
		/// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
		public int[] GetInts(int min, int max) => GetInts((int)GetVarULong(), min, max);
		/// <summary>Retrieves an <see cref="int"/> array from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
		public int[] GetInts(int amount, int min, int max)
		{
			int[] array = new int[amount];
			ReadInts(amount, array, 0, min, max);
			return array;
		}
        /// <summary>Populates an <see cref="int"/> array with ints retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetInts(int[] intoArray, int startIndex = 0, int min = int.MinValue, int max = int.MaxValue) => GetInts((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates an <see cref="int"/> array with ints retrieved from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetInts(int amount, int[] intoArray, int startIndex = 0, int min = int.MinValue, int max = int.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, IntName), nameof(amount));

            ReadInts(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Retrieves a <see cref="uint"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public uint[] GetUInts(uint min = uint.MinValue, uint max = uint.MaxValue) => GetUInts((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="uint"/> array from the message.</summary>
        /// <param name="amount">The amount of uints to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public uint[] GetUInts(int amount, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            uint[] array = new uint[amount];
            ReadUInts(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="uint"/> array with uints retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetUInts(uint[] intoArray, int startIndex = 0, uint min = uint.MinValue, uint max = uint.MaxValue) => GetUInts((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="uint"/> array with uints retrieved from the message.</summary>
        /// <param name="amount">The amount of uints to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetUInts(int amount, uint[] intoArray, int startIndex = 0, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, UIntName), nameof(amount));

            ReadUInts(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Reads a number of ints from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ints to read.</param>
        /// <param name="intoArray">The array to write the ints into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadInts(int amount, int[] intoArray, int startIndex, int min, int max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetInt(min, max);
            }
        }

        /// <summary>Reads a number of uints from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of uints to read.</param>
        /// <param name="intoArray">The array to write the uints into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadUInts(int amount, uint[] intoArray, int startIndex, uint min, uint max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetUInt(min, max);
            }
        }
        #endregion

        #region Long & ULong
		/// <summary>Adds a <see cref="ulong"/> to the message.</summary>
        /// <param name="value">The <see cref="ulong"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="ulong"/> was added to.</returns>
		public Message AddULong(ulong value, ulong min = ulong.MinValue, ulong max = ulong.MaxValue) {
			if(value > max || value < min) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {min} and {max} (inclusive)");
			if(writeValue <= data) throw new ArgumentException("First read all the data of a message before adding new data", nameof(data));
			data.Add(writeValue, value - min);
			if(max - min >= (ulong.MaxValue >> 1)) writeValue.LeftShift1ULong();
			else writeValue.Mult(max - min + 1);
			return this;
		}

		/// <summary>Retrieves a <see cref="ulong"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="ulong"/> that was retrieved.</returns>
		public ulong GetULong(ulong min = ulong.MinValue, ulong max = ulong.MaxValue) {
			if(min > max) throw new ArgumentOutOfRangeException(nameof(min), "min must be <= max");
			ulong value;
			ulong dif = max - min;
			if(dif++ == ulong.MaxValue) TakeBits(64);
			else if(dif.IsPowerOf2()) TakeBits(dif.Log2());
			else {
				ResetReadBit();
				value = data.DivReturnMod(dif);
			}
			void TakeBits(int bitCount) {
				value = data.TakeBits(readBit, bitCount);
				readBit += bitCount;
			}
			return value + min;
		}

		/// <summary>Adds a <see cref="long"/> to the message.</summary>
        /// <param name="value">The <see cref="long"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the <see cref="long"/> was added to.</returns>
		public Message AddLong(long value, long min = long.MinValue, long max = long.MaxValue)
			=> AddULong(value.Conv(), min.Conv(), max.Conv());

		/// <summary>Retrieves a <see cref="long"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The <see cref="long"/> that was retrieved.</returns>
		public long GetLong(long min = long.MinValue, long max = long.MaxValue)
			=> GetULong(min.Conv(), max.Conv()).Conv();

        /// <summary>Adds a <see cref="long"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddLongs(long[] array, bool includeLength = true, long min = long.MinValue, long max = long.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddLong(array[i], min, max);
            }

            return this;
        }

        /// <summary>Adds a <see cref="ulong"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddULongs(ulong[] array, bool includeLength = true, ulong min = ulong.MinValue, ulong max = ulong.MaxValue)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddULong(array[i], min, max);
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="long"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public long[] GetLongs(long min = long.MinValue, long max = long.MaxValue) => GetLongs((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="long"/> array from the message.</summary>
        /// <param name="amount">The amount of longs to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public long[] GetLongs(int amount, long min = long.MinValue, long max = long.MaxValue)
        {
            long[] array = new long[amount];
            ReadLongs(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="long"/> array with longs retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetLongs(long[] intoArray, int startIndex = 0, long min = long.MinValue, long max = long.MaxValue) => GetLongs((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="long"/> array with longs retrieved from the message.</summary>
        /// <param name="amount">The amount of longs to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetLongs(int amount, long[] intoArray, int startIndex = 0, long min = long.MinValue, long max = long.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, LongName), nameof(amount));

            ReadLongs(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Retrieves a <see cref="ulong"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public ulong[] GetULongs(ulong min = ulong.MinValue, ulong max = ulong.MaxValue) => GetULongs((int)GetVarULong(), min, max);
        /// <summary>Retrieves a <see cref="ulong"/> array from the message.</summary>
        /// <param name="amount">The amount of ulongs to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        /// <returns>The array that was retrieved.</returns>
        public ulong[] GetULongs(int amount, ulong min = ulong.MinValue, ulong max = ulong.MaxValue)
        {
            ulong[] array = new ulong[amount];
            ReadULongs(amount, array, 0, min, max);
            return array;
        }
        /// <summary>Populates a <see cref="ulong"/> array with ulongs retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetULongs(ulong[] intoArray, int startIndex = 0, ulong min = ulong.MinValue, ulong max = ulong.MaxValue) => GetULongs((int)GetVarULong(), intoArray, startIndex, min, max);
        /// <summary>Populates a <see cref="ulong"/> array with ulongs retrieved from the message.</summary>
        /// <param name="amount">The amount of ulongs to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        public void GetULongs(int amount, ulong[] intoArray, int startIndex = 0, ulong min = ulong.MinValue, ulong max = ulong.MaxValue)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, ULongName), nameof(amount));

            ReadULongs(amount, intoArray, startIndex, min, max);
        }

        /// <summary>Reads a number of longs from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of longs to read.</param>
        /// <param name="intoArray">The array to write the longs into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadLongs(int amount, long[] intoArray, int startIndex, long min, long max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetLong(min, max);
            }
        }

        /// <summary>Reads a number of ulongs from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of ulongs to read.</param>
        /// <param name="intoArray">The array to write the ulongs into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
        private void ReadULongs(int amount, ulong[] intoArray, int startIndex, ulong min, ulong max)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetULong(min, max);
            }
        }
        #endregion

        #region Float
		/// <summary>Adds a <see cref="float"/> to the message.</summary>
		/// <param name="value">The <see cref="float"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
		/// <returns>The message that the <see cref="float"/> was added to.</returns>
		/// <remarks>This is not very compact when min to max goes through 0 since about 50% of possible floats are between -0.01 and 0.01. Consider using AddFixedPoint() instead.</remarks>
		public Message AddFloat(float value, float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true) {
			if(!value.IsRealNumber() && !acceptInfAndNaN) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be a valid number instead of {value}");
			if(!min.IsRealNumber() || !max.IsRealNumber()) throw new ArgumentOutOfRangeException(nameof(min), "min and max must be valid numbers");
			if(mantissaBits < 0 || mantissaBits > 23) throw new ArgumentOutOfRangeException(nameof(mantissaBits), "Bits of accuracy must be between 1 and 23 (inclusive)");
			int shift = 23 - mantissaBits;
			uint val = value.ConvUInt() >> shift;
			uint maxUI = max.ConvUInt() >> shift;
			uint minUI = min.ConvUInt() >> shift;
			if(acceptInfAndNaN) maxUI += 3;
			if(acceptInfAndNaN && !value.IsRealNumber()) {
				if(float.IsNaN(value)) val = maxUI;
				else if(float.IsPositiveInfinity(value)) val = maxUI - 1;
				else if(float.IsNegativeInfinity(value)) val = maxUI - 2;
				else throw new Exception($"Value is not a real number but neither nan nor any infinity: {value}");
			} else if(val > maxUI || val < minUI) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {min} and {max} instead of {value}");
			return AddUInt(val, minUI, maxUI);
		}

		/// <summary>Retrieves a <see cref="float"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
		/// <returns>The <see cref="float"/> that was retrieved.</returns>
		public float GetFloat(float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true) {
			if(min > max) throw new ArgumentOutOfRangeException(nameof(min), $"min {min} must be less than or equal to max {max}");
			if(!min.IsRealNumber() || !max.IsRealNumber()) throw new ArgumentOutOfRangeException(nameof(min), "min and max must be valid numbers");
			if(mantissaBits < 0 || mantissaBits > 23) throw new ArgumentOutOfRangeException(nameof(mantissaBits), "Bits of accuracy must be between 1 and 23 (inclusive)");
			int shift = 23 - mantissaBits;
			uint maxUI = max.ConvUInt() >> shift;
			if(acceptInfAndNaN) maxUI += 3;
			uint value = GetUInt(min.ConvUInt() >> shift, maxUI) << shift;
			float f = value.ConvFloat();
			if(acceptInfAndNaN) {
				if(value == maxUI) f = float.NaN;
				else if(value == maxUI - 1) f = float.PositiveInfinity;
				else if(value == maxUI - 2) f = float.NegativeInfinity;
			} else if(!f.IsRealNumber()) throw new Exception("Value is not a valid number");
			return f;
		}

        /// <summary>Adds a <see cref="float"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddFloats(float[] array, bool includeLength = true, float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddFloat(array[i], min ,max, mantissaBits, acceptInfAndNaN);
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="float"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The array that was retrieved.</returns>
        public float[] GetFloats(float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true)
			=> GetFloats((int)GetVarULong(), min, max, mantissaBits, acceptInfAndNaN);
        /// <summary>Retrieves a <see cref="float"/> array from the message.</summary>
        /// <param name="amount">The amount of floats to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The array that was retrieved.</returns>
        public float[] GetFloats(int amount, float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true)
        {
            float[] array = new float[amount];
            ReadFloats(amount, array, 0, min, max, mantissaBits, acceptInfAndNaN);
            return array;
        }
        /// <summary>Populates a <see cref="float"/> array with floats retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        public void GetFloats(float[] intoArray, int startIndex = 0, float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true)
			=> GetFloats((int)GetVarULong(), intoArray, startIndex, min, max, mantissaBits, acceptInfAndNaN);
        /// <summary>Populates a <see cref="float"/> array with floats retrieved from the message.</summary>
        /// <param name="amount">The amount of floats to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        public void GetFloats(int amount, float[] intoArray, int startIndex = 0, float min = float.MinValue, float max = float.MaxValue, int mantissaBits = 23, bool acceptInfAndNaN = true)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, FloatName), nameof(amount));

            ReadFloats(amount, intoArray, startIndex, min, max, mantissaBits, acceptInfAndNaN);
        }

        /// <summary>Reads a number of floats from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of floats to read.</param>
        /// <param name="intoArray">The array to write the floats into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        private void ReadFloats(int amount, float[] intoArray, int startIndex, float min, float max, int mantissaBits, bool acceptInfAndNaN)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetFloat(min, max, mantissaBits, acceptInfAndNaN);
            }
        }
        #endregion

        #region Double
		/// <summary>Adds a <see cref="double"/> to the message.</summary>
		/// <param name="value">The <see cref="double"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
		/// <returns>The message that the <see cref="double"/> was added to.</returns>
		/// <remarks>This is not very compact when min to max goes through 0 since about 50% of possible doubles are between -0.01 and 0.01. Consider using AddFixedPoint() instead.</remarks>
		public Message AddDouble(double value, double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true) {
			if(!value.IsRealNumber() && !acceptInfAndNaN) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be a valid number instead of {value}");
			if(!min.IsRealNumber() || !max.IsRealNumber()) throw new ArgumentOutOfRangeException(nameof(min), "min and max must be valid numbers");
			if(mantissaBits < 0 || mantissaBits > 52) throw new ArgumentOutOfRangeException(nameof(mantissaBits), "Bits of accuracy must be between 1 and 23 (inclusive)");
			int shift = 52 - mantissaBits;
			ulong val = value.ConvULong() >> shift;
			ulong maxUL = max.ConvULong() >> shift;
			ulong minUL = min.ConvULong() >> shift;
			if(acceptInfAndNaN) maxUL += 3;
			if(acceptInfAndNaN && !value.IsRealNumber()) {
				if(double.IsNaN(value)) val = maxUL;
				else if(double.IsPositiveInfinity(value)) val = maxUL - 1;
				else if(double.IsNegativeInfinity(value)) val = maxUL - 2;
				else throw new Exception($"Value is not a real number but neither nan nor any infinity: {value}");
			} else if(val > maxUL || val < minUL) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {min} and {max} (inclusive) but it is {value}");
			return AddULong(val, minUL, maxUL);
		}

		/// <summary>Retrieves a <see cref="double"/> from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
		/// <returns>The <see cref="double"/> that was retrieved.</returns>
		public double GetDouble(double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true) {
			if(min > max) throw new ArgumentOutOfRangeException(nameof(min), $"min {min} must be less than or equal to max {max}");
			if(!min.IsRealNumber() || !max.IsRealNumber()) throw new ArgumentOutOfRangeException(nameof(min), "min and max must be valid numbers");
			if(mantissaBits < 0 || mantissaBits > 52) throw new ArgumentOutOfRangeException(nameof(mantissaBits), "Bits of accuracy must be between 1 and 23 (inclusive)");
			int shift = 52 - mantissaBits;
			ulong maxUL = max.ConvULong() >> shift;
			if(acceptInfAndNaN) maxUL += 3;
			ulong value = GetULong(min.ConvULong() >> shift, maxUL) << shift;
			double d = value.ConvDouble();
			if(acceptInfAndNaN) {
				if(value == maxUL) d = double.NaN;
				else if(value == maxUL - 1) d = double.PositiveInfinity;
				else if(value == maxUL - 2) d = double.NegativeInfinity;
			} else if(!d.IsRealNumber()) throw new Exception("Value is not a valid number");
			return d;
		}

        /// <summary>Adds a <see cref="double"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddDoubles(double[] array, bool includeLength = true, double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                AddDouble(array[i], min, max, mantissaBits, acceptInfAndNaN);
            }

            return this;
        }

        /// <summary>Retrieves a <see cref="double"/> array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The array that was retrieved.</returns>
        public double[] GetDoubles(double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true)
			=> GetDoubles((int)GetVarULong(), min, max, mantissaBits, acceptInfAndNaN);
        /// <summary>Retrieves a <see cref="double"/> array from the message.</summary>
        /// <param name="amount">The amount of doubles to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        /// <returns>The array that was retrieved.</returns>
        public double[] GetDoubles(int amount, double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true)
        {
            double[] array = new double[amount];
            ReadDoubles(amount, array, 0, min, max, mantissaBits, acceptInfAndNaN);
            return array;
        }
        /// <summary>Populates a <see cref="double"/> array with doubles retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        public void GetDoubles(double[] intoArray, int startIndex = 0, double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true)
			=> GetDoubles((int)GetVarULong(), intoArray, startIndex, min, max, mantissaBits, acceptInfAndNaN);
        /// <summary>Populates a <see cref="double"/> array with doubles retrieved from the message.</summary>
        /// <param name="amount">The amount of doubles to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        public void GetDoubles(int amount, double[] intoArray, int startIndex = 0, double min = double.MinValue, double max = double.MaxValue, int mantissaBits = 52, bool acceptInfAndNaN = true)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, DoubleName), nameof(amount));

            ReadDoubles(amount, intoArray, startIndex, min, max, mantissaBits, acceptInfAndNaN);
        }

        /// <summary>Reads a number of doubles from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of doubles to read.</param>
        /// <param name="intoArray">The array to write the doubles into.</param>
        /// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="mantissaBits">The amount of mantissa bits. This always rounds towards 0.</param>
		/// <param name="acceptInfAndNaN">Whether to accept numbers like inf or nan.</param>
        private void ReadDoubles(int amount, double[] intoArray, int startIndex, double min, double max, int mantissaBits, bool acceptInfAndNaN)
        {
            for (int i = 0; i < amount; i++)
            {
                intoArray[startIndex + i] = GetDouble(min, max, mantissaBits, acceptInfAndNaN);
            }
        }
        #endregion

		#region FixedPoint
		/// <summary>Adds a fixed point number to the message.</summary>
		/// <param name="value">The <see cref="decimal"/> to add.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		/// <returns>The message that the <see cref="decimal"/> was added to.</returns>
		public Message AddFixedPoint(decimal value, decimal min, decimal max, decimal stepSize) {
			if(value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {min} and {max} (inclusive) but it is {value}");
			if(stepSize <= 0) throw new ArgumentOutOfRangeException(nameof(stepSize), "Step size must be greater than 0");
			decimal dif = max - min;
			if(dif == 0m) return this;
			decimal steps = Math.Ceiling(dif / stepSize);
			if(steps > ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(stepSize), $"Step size {stepSize} is too small for the range: {dif} of min: {min} and max: {max}");
			ulong val = (ulong)Math.Round((value - min) * steps / dif);
			return AddULong(val, 0UL, (ulong)steps);
		}

		/// <summary>Retrieves a fixed point number from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		/// <returns>The <see cref="decimal"/> that was retrieved.</returns>
		public decimal GetFixedPoint(decimal min, decimal max, decimal stepSize) {
			if(min > max) throw new ArgumentOutOfRangeException(nameof(min), $"min {min} must be less than or equal to max {max}");
			if(stepSize <= 0) throw new ArgumentOutOfRangeException(nameof(stepSize), "Step size must be greater than 0");
			decimal dif = max - min;
			decimal steps = Math.Ceiling(dif / stepSize);
			if(steps > ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(stepSize), $"Step size {stepSize} is too small for the range: {dif} of min: {min} and max: {max}");
			ulong val = GetULong(0, (ulong)steps);
			return min + dif * val / steps;
		}

		/// <summary>Adds a fixed point number array to the message.</summary>
		/// <param name="array">The array to add.</param>
		/// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		/// <returns>The message that the array was added to.</returns>
		public Message AddFixedPoints(decimal[] array, decimal min, decimal max, decimal stepSize, bool includeLength = true) {
			if(includeLength)
				AddVarULong((uint)array.Length);

			for(int i = 0; i < array.Length; i++)
				AddFixedPoint(array[i], min, max, stepSize);

			return this;
		}

		/// <summary>Retrieves a fixed point number array from the message.</summary>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		/// <returns>The array that was retrieved.</returns>
		public decimal[] GetFixedPoints(decimal min, decimal max, decimal stepSize)
			=> GetFixedPoints((int)GetVarULong(), min, max, stepSize);

		/// <summary>Retrieves a fixed point number array from the message.</summary>
		/// <param name="amount">The amount of fixed point numbers to retrieve.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		/// <returns>The array that was retrieved.</returns>
		public decimal[] GetFixedPoints(int amount, decimal min, decimal max, decimal stepSize) {
			decimal[] array = new decimal[amount];
			ReadFixedPoints(amount, array, 0, min, max, stepSize);
			return array;
		}

		/// <summary>Populates a fixed point number array with fixed point numbers retrieved from the message.</summary>
		/// <param name="intoArray">The array to populate.</param>
		/// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		public void GetFixedPoints(decimal[] intoArray, int startIndex, decimal min, decimal max, decimal stepSize)
			=> GetFixedPoints((int)GetVarULong(), intoArray, startIndex, min, max, stepSize);

		/// <summary>Populates a fixed point number array with fixed point numbers retrieved from the message.</summary>
		/// <param name="amount">The amount of fixed point numbers to retrieve.</param>
		/// <param name="intoArray">The array to populate.</param>
		/// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		public void GetFixedPoints(int amount, decimal[] intoArray, int startIndex, decimal min, decimal max, decimal stepSize) {
			if(startIndex + amount > intoArray.Length)
				throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, FixedPointName), nameof(amount));

			ReadFixedPoints(amount, intoArray, startIndex, min, max, stepSize);
		}

		/// <summary>Reads a number of fixed point numbers from the message and writes them into the given array.</summary>
		/// <param name="amount">The amount of fixed point numbers to read.</param>
		/// <param name="intoArray">The array to write the fixed point numbers into.</param>
		/// <param name="startIndex">The position at which to start writing into the array.</param>
		/// <param name="min">The minimum value.</param>
		/// <param name="max">The maximum value.</param>
		/// <param name="stepSize">The difference between each possible value.</param>
		private decimal[] ReadFixedPoints(int amount, decimal[] intoArray, int startIndex, decimal min, decimal max, decimal stepSize) {
			if(startIndex + amount > intoArray.Length)
				throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, FixedPointName), nameof(amount));

			for(int i = 0; i < amount; i++)
				intoArray[startIndex + i] = GetFixedPoint(min, max, stepSize);

			return intoArray;
		}
		#endregion

        #region String
        /// <summary>Adds a <see cref="string"/> to the message.</summary>
        /// <param name="value">The <see cref="string"/> to add.</param>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to make before sending.</param>
        /// <returns>The message that the <see cref="string"/> was added to.</returns>
        public Message AddString(string value, byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
        {
			if(max < byte.MaxValue && max > (byte.MaxValue >> 1)) throw new ArgumentException($"max is incompatible with utf8: {max}");
			value = Helper.SwapValues(value, replacements);
            return AddBytes(Encoding.UTF8.GetBytes(value), true, byte.MinValue, max);
        }

        /// <summary>Retrieves a <see cref="string"/> from the message.</summary>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to reverse before recieving</param>
        /// <returns>The <see cref="string"/> that was retrieved.</returns>
        public string GetString(byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
        {
			if(max < byte.MaxValue && max > (byte.MaxValue >> 1)) throw new ArgumentException($"max is incompatible with utf8: {max}");
            int length = (int)GetVarULong(); // Get the length of the string (in bytes, NOT characters)
			byte[] bytes = GetBytes(length, byte.MinValue, max);
            string value = Encoding.UTF8.GetString(bytes, 0, length);
            return Helper.SwapValues(value, replacements);
        }

        /// <summary>Adds a <see cref="string"/> array to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to make before sending.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddStrings(string[] array, bool includeLength = true, byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            // It'd be ideal to throw an exception here (instead of in AddString) if the entire array isn't going to fit, but since each string could
            // be (and most likely is) a different length and some characters use more than a single byte, the only way of doing that would be to loop
            // through the whole array here and convert each string to bytes ahead of time, just to get the required byte count. Then if they all fit
            // into the message, they would all be converted again when actually being written into the byte array, which is obviously inefficient.

            for (int i = 0; i < array.Length; i++)
                AddString(array[i], max, replacements);

            return this;
        }

        /// <summary>Retrieves a <see cref="string"/> array from the message.</summary>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to reverse before recieving</param>
        /// <returns>The array that was retrieved.</returns>
        public string[] GetStrings(byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
			=> GetStrings((int)GetVarULong(), max, replacements);
        /// <summary>Retrieves a <see cref="string"/> array from the message.</summary>
        /// <param name="amount">The amount of strings to retrieve.</param>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to reverse before recieving</param>
        /// <returns>The array that was retrieved.</returns>
        public string[] GetStrings(int amount, byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
        {
            string[] array = new string[amount];
            for (int i = 0; i < array.Length; i++)
                array[i] = GetString(max, replacements);

            return array;
        }
        /// <summary>Populates a <see cref="string"/> array with strings retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to reverse before recieving</param>
        public void GetStrings(string[] intoArray, int startIndex = 0, byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
			=> GetStrings((int)GetVarULong(), intoArray, startIndex, max, replacements);
        /// <summary>Populates a <see cref="string"/> array with strings retrieved from the message.</summary>
        /// <param name="amount">The amount of strings to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
		/// <param name="max">The maximum possible char of the string.</param>
		/// <param name="replacements">The character replacements to reverse before recieving</param>
        public void GetStrings(int amount, string[] intoArray, int startIndex = 0, byte max = byte.MaxValue, (char val, char rep)[] replacements = null)
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, StringName), nameof(amount));

            for (int i = 0; i < amount; i++)
                intoArray[startIndex + i] = GetString(max, replacements);
        }
        #endregion

		#region States of T
		/// <summary>Adds one of the possible values.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="element"></param>
		/// <param name="possibleValues"></param>
		public Message AddElement<T>(T element, T[] possibleValues) {
			if (possibleValues == null || possibleValues.Length == 0)
				throw new ArgumentException("Possible values array cannot be null or empty", nameof(possibleValues));

			int index = Array.IndexOf(possibleValues, element);
			if (index == -1)
				throw new ArgumentException($"Element {element} is not a valid value for this message", nameof(element));

			return AddInt(index, 0, possibleValues.Length - 1);
		}

		/// <summary>Retrieves one of the possible values.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="possibleValues"></param>
		/// <returns>The retrieved element.</returns>
		public T GetElement<T>(T[] possibleValues) {
			if (possibleValues == null || possibleValues.Length == 0)
				throw new ArgumentException("Possible values array cannot be null or empty", nameof(possibleValues));

			int index = GetInt(0, possibleValues.Length - 1);
			if (index < 0 || index >= possibleValues.Length)
				throw new Exception($"Received invalid index {index} for possible values array of length {possibleValues.Length}");

			return possibleValues[index];
		}

		/// <summary>Adds a T array message.</summary>
        /// <param name="elements">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
		/// <param name="possibleValues">The possible values of the elements in elements.</param>
        /// <returns>The message that the array was added to.</returns>
		public Message AddElements<T>(T[] elements, T[] possibleValues, bool includeLength = true) {
			if (includeLength)
                AddVarULong((uint)elements.Length);

			for (int i = 0; i < elements.Length; i++)
				AddElement(elements[i], possibleValues);

			return this;
		}

		/// <summary>Retrieves a T array from the message.</summary>
		/// <param name="possibleValues">The possible values of the elements in elements.</param>
        /// <returns>The array that was retrieved.</returns>
		public T[] GetElements<T>(T[] possibleValues) => GetElements((int)GetVarULong(), possibleValues);

		/// <summary>Retrieves a T array from the message.</summary>
        /// <param name="amount">The amount of ints to retrieve.</param>
		/// <param name="possibleValues">The possible values of the elements in elements.</param>
        /// <returns>The array that was retrieved.</returns>
		public T[] GetElements<T>(int amount, T[] possibleValues) {
			T[] elements = new T[amount];
			for (int i = 0; i < amount; i++)
				elements[i] = GetElement(possibleValues);

			return elements;
		}
		#endregion

        #region IMessageSerializable Types
        /// <summary>Adds a serializable to the message.</summary>
        /// <param name="value">The serializable to add.</param>
        /// <returns>The message that the serializable was added to.</returns>
        public Message AddSerializable<T>(T value) where T : IMessageSerializable
        {
            value.Serialize(this);
            return this;
        }

        /// <summary>Retrieves a serializable from the message.</summary>
        /// <returns>The serializable that was retrieved.</returns>
        public T GetSerializable<T>() where T : IMessageSerializable, new()
        {
            T t = new T();
            t.Deserialize(this);
            return t;
        }

        /// <summary>Adds an array of serializables to the message.</summary>
        /// <param name="array">The array to add.</param>
        /// <param name="includeLength">Whether or not to include the length of the array in the message.</param>
        /// <returns>The message that the array was added to.</returns>
        public Message AddSerializables<T>(T[] array, bool includeLength = true) where T : IMessageSerializable
        {
            if (includeLength)
                AddVarULong((uint)array.Length);

            for (int i = 0; i < array.Length; i++)
                AddSerializable(array[i]);

            return this;
        }

        /// <summary>Retrieves an array of serializables from the message.</summary>
        /// <returns>The array that was retrieved.</returns>
        public T[] GetSerializables<T>() where T : IMessageSerializable, new() => GetSerializables<T>((int)GetVarULong());
        /// <summary>Retrieves an array of serializables from the message.</summary>
        /// <param name="amount">The amount of serializables to retrieve.</param>
        /// <returns>The array that was retrieved.</returns>
        public T[] GetSerializables<T>(int amount) where T : IMessageSerializable, new()
        {
            T[] array = new T[amount];
            ReadSerializables(amount, array);
            return array;
        }
        /// <summary>Populates an array of serializables retrieved from the message.</summary>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetSerializables<T>(T[] intoArray, int startIndex = 0) where T : IMessageSerializable, new() => GetSerializables<T>((int)GetVarULong(), intoArray, startIndex);
        /// <summary>Populates an array of serializables retrieved from the message.</summary>
        /// <param name="amount">The amount of serializables to retrieve.</param>
        /// <param name="intoArray">The array to populate.</param>
        /// <param name="startIndex">The position at which to start populating the array.</param>
        public void GetSerializables<T>(int amount, T[] intoArray, int startIndex = 0) where T : IMessageSerializable, new()
        {
            if (startIndex + amount > intoArray.Length)
                throw new ArgumentException(ArrayNotLongEnoughError(amount, intoArray.Length, startIndex, typeof(T).Name), nameof(amount));

            ReadSerializables(amount, intoArray, startIndex);
        }

        /// <summary>Reads a number of serializables from the message and writes them into the given array.</summary>
        /// <param name="amount">The amount of serializables to read.</param>
        /// <param name="intoArray">The array to write the serializables into.</param>
        /// <param name="startIndex">The position at which to start writing into <paramref name="intoArray"/>.</param>
        private void ReadSerializables<T>(int amount, T[] intoArray, int startIndex = 0) where T : IMessageSerializable, new()
        {
            for (int i = 0; i < amount; i++)
                intoArray[startIndex + i] = GetSerializable<T>();
        }
        #endregion

        #region Overload Versions
        /// <inheritdoc cref="AddByte(byte, byte, byte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddByte(byte, byte, byte)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(byte value) => AddByte(value);
        /// <inheritdoc cref="AddSByte(sbyte, sbyte, sbyte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSByte(sbyte, sbyte, sbyte)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(sbyte value) => AddSByte(value);
        /// <inheritdoc cref="AddBool(bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBool(bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(bool value) => AddBool(value);
        /// <inheritdoc cref="AddShort(short, short, short)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddShort(short, short, short)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(short value) => AddShort(value);
        /// <inheritdoc cref="AddUShort(ushort, ushort, ushort)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUShort(ushort, ushort, ushort)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(ushort value) => AddUShort(value);
        /// <inheritdoc cref="AddInt(int, int, int)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddInt(int, int, int)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(int value) => AddInt(value);
        /// <inheritdoc cref="AddUInt(uint, uint, uint)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUInt(uint, uint, uint)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(uint value) => AddUInt(value);
        /// <inheritdoc cref="AddLong(long, long, long)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddLong(long, long, long)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(long value) => AddLong(value);
        /// <inheritdoc cref="AddULong(ulong, ulong, ulong)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddULong(ulong, ulong, ulong)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(ulong value) => AddULong(value);
        /// <inheritdoc cref="AddFloat(float, float, float, int, bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddFloat(float, float, float, int, bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(float value) => AddFloat(value);
        /// <inheritdoc cref="AddDouble(double, double, double, int, bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddDouble(double, double, double, int, bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(double value) => AddDouble(value);
        /// <inheritdoc cref="AddString(string, byte, ValueTuple{char, char}[])"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddString(string, byte, ValueTuple{char, char}[])"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(string value) => AddString(value);
        /// <inheritdoc cref="AddSerializable{T}(T)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSerializable{T}(T)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add<T>(T value) where T : IMessageSerializable => AddSerializable(value);

        /// <inheritdoc cref="AddBytes(byte[], bool, byte, byte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBytes(byte[], bool, byte, byte)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(byte[] array, bool includeLength = true) => AddBytes(array, includeLength);
        /// <inheritdoc cref="AddSBytes(sbyte[], bool, sbyte, sbyte)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSBytes(sbyte[], bool, sbyte, sbyte)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(sbyte[] array, bool includeLength = true) => AddSBytes(array, includeLength);
        /// <inheritdoc cref="AddBools(bool[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddBools(bool[], bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(bool[] array, bool includeLength = true) => AddBools(array, includeLength);
        /// <inheritdoc cref="AddShorts(short[], bool, short, short)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddShorts(short[], bool, short, short)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(short[] array, bool includeLength = true) => AddShorts(array, includeLength);
        /// <inheritdoc cref="AddUShorts(ushort[], bool, ushort, ushort)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUShorts(ushort[], bool, ushort, ushort)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(ushort[] array, bool includeLength = true) => AddUShorts(array, includeLength);
        /// <inheritdoc cref="AddInts(int[], bool, int, int)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddInts(int[], bool, int, int)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(int[] array, bool includeLength = true) => AddInts(array, includeLength);
        /// <inheritdoc cref="AddUInts(uint[], bool, uint, uint)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddUInts(uint[], bool, uint, uint)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(uint[] array, bool includeLength = true) => AddUInts(array, includeLength);
        /// <inheritdoc cref="AddLongs(long[], bool, long, long)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddLongs(long[], bool, long, long)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(long[] array, bool includeLength = true) => AddLongs(array, includeLength);
        /// <inheritdoc cref="AddULongs(ulong[], bool, ulong, ulong)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddULongs(ulong[], bool, ulong, ulong)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(ulong[] array, bool includeLength = true) => AddULongs(array, includeLength);
        /// <inheritdoc cref="AddFloats(float[], bool, float, float, int, bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddFloats(float[], bool, float, float, int, bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(float[] array, bool includeLength = true) => AddFloats(array, includeLength);
        /// <inheritdoc cref="AddDoubles(double[], bool, double, double, int, bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddDoubles(double[], bool, double, double, int, bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(double[] array, bool includeLength = true) => AddDoubles(array, includeLength);
        /// <inheritdoc cref="AddStrings(string[], bool, byte, ValueTuple{char, char}[])"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddStrings(string[], bool, byte, ValueTuple{char, char}[])"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add(string[] array, bool includeLength = true) => AddStrings(array, includeLength);
        /// <inheritdoc cref="AddSerializables{T}(T[], bool)"/>
        /// <remarks>This method is simply an alternative way of calling <see cref="AddSerializables{T}(T[], bool)"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Message Add<T>(T[] array, bool includeLength = true) where T : IMessageSerializable, new() => AddSerializables(array, includeLength);
        #endregion
        #endregion

        #region Error Messaging
        /// <summary>The name of a <see cref="byte"/> value.</summary>
        private const string ByteName        = "byte";
        /// <summary>The name of a <see cref="sbyte"/> value.</summary>
        private const string SByteName       = "sbyte";
        /// <summary>The name of a <see cref="bool"/> value.</summary>
        private const string BoolName        = "bool";
        /// <summary>The name of a <see cref="short"/> value.</summary>
        private const string ShortName       = "short";
        /// <summary>The name of a <see cref="ushort"/> value.</summary>
        private const string UShortName      = "ushort";
        /// <summary>The name of an <see cref="int"/> value.</summary>
        private const string IntName         = "int";
        /// <summary>The name of a <see cref="uint"/> value.</summary>
        private const string UIntName        = "uint";
        /// <summary>The name of a <see cref="long"/> value.</summary>
        private const string LongName        = "long";
        /// <summary>The name of a <see cref="ulong"/> value.</summary>
        private const string ULongName       = "ulong";
        /// <summary>The name of a <see cref="float"/> value.</summary>
        private const string FloatName       = "float";
        /// <summary>The name of a <see cref="double"/> value.</summary>
        private const string DoubleName      = "double";
		/// <summary>The name of a fixed-point value.</summary>
		private const string FixedPointName  = "fixed-point";
        /// <summary>The name of a <see cref="string"/> value.</summary>
        private const string StringName      = "string";
        /// <summary>The name of an array length value.</summary>
        private const string ArrayLengthName = "array length";

        /// <summary>Constructs an error message for when a number of retrieved values do not fit inside the bounds of the provided array.</summary>
        /// <param name="amount">The number of values being retrieved.</param>
        /// <param name="arrayLength">The length of the provided array.</param>
        /// <param name="startIndex">The position in the array at which to begin writing values.</param>
        /// <param name="valueName">The name of the value type which is being retrieved.</param>
        /// <param name="pluralValueName">The name of the value type in plural form. If left empty, this will be set to <paramref name="valueName"/> with an <c>s</c> appended to it.</param>
        /// <returns>The error message.</returns>
        private static string ArrayNotLongEnoughError(int amount, int arrayLength, int startIndex, string valueName, string pluralValueName = "")
        {
            if (string.IsNullOrEmpty(pluralValueName))
                pluralValueName = $"{valueName}s";

            return $"The amount of {pluralValueName} to retrieve ({amount}) is greater than the number of elements from the start index ({startIndex}) to the end of the given array (length: {arrayLength})!";
        }
        #endregion
    }
}
