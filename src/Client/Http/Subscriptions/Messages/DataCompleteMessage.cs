namespace StrawberryShake.Http.Subscriptions.Messages
{
    public sealed class DataCompleteMessage
        : OperationMessage
    {
        public DataCompleteMessage(string id)
            : base(MessageTypes.Subscription.Complete, id)
        {
        }
    }
}
