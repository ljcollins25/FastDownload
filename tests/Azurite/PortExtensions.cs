// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable disable

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Shouldly;

namespace FastDownload.Tests.Azurite;

public static class PortExtensions
{
    private static readonly ConcurrentDictionary<int, bool> PortCollection = new ConcurrentDictionary<int, bool>();

    static PortExtensions()
    {
        PortCollection.GetOrAdd(0, true);
    }

    public static int GetNextAvailablePort()
    {
        int portNumber = 0;
        while (PortCollection.ContainsKey(portNumber))
        {
            using (Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp))
            {
                var endPoint = new IPEndPoint(IPAddress.Loopback, 0);
                socket.Bind(endPoint);
                portNumber = socket.LocalEndPoint.ShouldBeOfType<IPEndPoint>().Port;
                portNumber.ShouldNotBe(0);

                if (PortCollection.TryAdd(portNumber, true))
                {
                    return portNumber;
                }
            }
        }

        return portNumber;
    }
}
