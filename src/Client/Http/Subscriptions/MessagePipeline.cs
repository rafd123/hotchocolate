using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Language;
using StrawberryShake.Http.Subscriptions.Messages;

namespace StrawberryShake.Http.Subscriptions
{
    internal class MessagePipeline
        : IMessagePipeline
    {
        private readonly IMessageHandler[] _messageHandlers;

        public MessagePipeline(IEnumerable<IMessageHandler> messageHandlers)
        {
            if (messageHandlers is null)
            {
                throw new ArgumentNullException(nameof(messageHandlers));
            }

            _messageHandlers = messageHandlers.ToArray();
        }

        public async Task ProcessAsync(
            ISocketConnection connection,
            ReadOnlySequence<byte> slice,
            CancellationToken cancellationToken)
        {
            if (TryParseMessage(slice, out OperationMessage message))
            {
                await HandleMessageAsync(connection, message, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await connection.SendAsync(
                    KeepConnectionAliveMessage.Default.Serialize(),
                    CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private static bool TryParseMessage(
            ReadOnlySequence<byte> slice,
            out OperationMessage? message)
        {
            ReadOnlySpan<byte> messageData;
            byte[] buffer = null;

            if (slice.IsSingleSegment)
            {
                messageData = slice.First.Span;
            }
            else
            {
                buffer = ArrayPool<byte>.Shared.Rent(1024 * 4);
                int buffered = 0;

                SequencePosition position = slice.Start;
                ReadOnlyMemory<byte> memory;

                while (slice.TryGet(ref position, out memory, true))
                {
                    ReadOnlySpan<byte> span = memory.Span;
                    var bytesRemaining = buffer.Length - buffered;

                    if (span.Length > bytesRemaining)
                    {
                        var next = ArrayPool<byte>.Shared.Rent(
                            buffer.Length * 2);
                        Buffer.BlockCopy(buffer, 0, next, 0, buffer.Length);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = next;
                    }

                    for (int i = 0; i < span.Length; i++)
                    {
                        buffer[buffered++] = span[i];
                    }
                }

                messageData = buffer;
                messageData = messageData.Slice(0, buffered);
            }

            try
            {
                if (messageData.Length == 0
                    || (messageData.Length == 1 && messageData[0] == default))
                {
                    message = null;
                    return false;
                }

                GraphQLSocketMessage parsedMessage =
                    Utf8GraphQLRequestParser.ParseMessage(messageData);
                return TryDeserializeMessage(parsedMessage, out message);
            }
            catch (SyntaxException)
            {
                message = null;
                return false;
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private static bool TryDeserializeMessage(
            GraphQLSocketMessage parsedMessage,
            out OperationMessage? message)
        {
            switch (parsedMessage.Type)
            {
                case MessageTypes.Connection.Error:
                    return true;

                case MessageTypes.Connection.Accept:
                    message = AcceptConnectionMessage.Default;
                    return true;

                case MessageTypes.Subscription.Data:
                    return true;

                case MessageTypes.Subscription.Error:
                    return true;

                case MessageTypes.Subscription.Complete:
                    message = DeserializeSubscriptionCompleteMessage(parsedMessage);
                    return true;

                default:
                    message = null;
                    return false;
            }
        }

        private static DataCompleteMessage DeserializeSubscriptionCompleteMessage(
            GraphQLSocketMessage parsedMessage)
        {
            if (parsedMessage.Id is null)
            {
                throw new InvalidOperationException(
                    "Invalid message structure.");
            }
            return new DataCompleteMessage(parsedMessage.Id);
        }

        private static bool TryDeserializeDataStartMessage(
            GraphQLSocketMessage parsedMessage,
            out OperationMessage message)
        {
            if (!parsedMessage.HasPayload)
            {
                message = null;
                return false;
            }

            IReadOnlyList<GraphQLRequest> batch =
                Utf8GraphQLRequestParser.Parse(parsedMessage.Payload);

            message = new DataStartMessage(parsedMessage.Id, batch[0]);
            return true;
        }

        private static DataStopMessage DeserializeDataStopMessage(
            GraphQLSocketMessage parsedMessage)
        {
            if (parsedMessage.HasPayload)
            {
                throw new InvalidOperationException(
                    "Invalid message structure.");
            }

            return new DataStopMessage(parsedMessage.Id);
        }

        private async Task HandleMessageAsync(
            ISocketConnection connection,
            OperationMessage message,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < _messageHandlers.Length; i++)
            {
                IMessageHandler handler = _messageHandlers[i];

                if (handler.CanHandle(message))
                {
                    await handler.HandleAsync(
                            connection,
                            message,
                            cancellationToken)
                        .ConfigureAwait(false);

                    // the message is handled and we are done.
                    return;
                }
            }

            // TODO : resources
            throw new NotSupportedException(
                "The specified message type is not supported.");
        }
    }
}
