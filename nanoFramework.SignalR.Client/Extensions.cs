namespace nanoFramework.SignalR.Client
{
    /// <summary>
    /// SignalR Extensions
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// Send a message to the server.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="methodName"></param>
        /// <param name="arg1"></param>
        /// <param name="timeout"></param>
        public static void InvokeCoreAsync(this HubConnection connection, string methodName, object arg1, int timeout = 0)
            => connection.InvokeCoreAsync(methodName, null, new[] { arg1 }, timeout);

        /// <summary>
        /// Send a message to the server.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="methodName"></param>
        /// <param name="arg1"></param>
        /// <param name="arg2"></param>
        /// <param name="timeout"></param>
        public static void InvokeCoreAsync(this HubConnection connection, string methodName, object arg1, object arg2, int timeout = 0)
            => connection.InvokeCoreAsync(methodName, null, new[] { arg1, arg2 }, timeout);

        /// <summary>
        /// Send a message to the server.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="methodName"></param>
        /// <param name="arg1"></param>
        /// <param name="arg2"></param>
        /// <param name="arg3"></param>
        /// <param name="timeout"></param>
        public static void InvokeCoreAsync(this HubConnection connection, string methodName, object arg1, object arg2, object arg3, int timeout = 0)
            => connection.InvokeCoreAsync(methodName, null, new[] { arg1, arg2, arg3 }, timeout);

    }
}
