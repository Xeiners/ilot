using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MyNotch.Modules.DropZone.Local;

/// <summary>
/// Gives the PC a name on the local network while the sharing is open, "mynotch.local", so a phone can type that
/// rather than an IP address: a minimal mDNS responder (RFC 6762) answering the questions for that name with the
/// PC's address. No library, no service to install: one UDP socket on 5353, shared with Windows' own responder.
/// </summary>
/// <remarks>
/// The name is first asked for on the network: if another PC already answers to it (another MyNotch), the next one
/// is taken ("mynotch-2.local"…). Once claimed it is announced, and withdrawn on <see cref="Dispose"/> (a record
/// with a zero lifetime, so phones forget it at once). Phones resolve <c>.local</c> names themselves: iPhone, Mac,
/// Windows, Linux and recent Android do; the IP address stays shown for the others.
/// </remarks>
internal sealed class MdnsResponder : IDisposable
{
    private const int Port = 5353;
    private const int SioUdpConnReset = -1744830452;
    private const ushort TypeA = 1;
    private const ushort TypeAny = 255;
    private const ushort ClassIn = 1;
    private const ushort CacheFlush = 0x8000; // In answers: this record replaces any other for the name.
    private const ushort UnicastResponse = 0x8000; // In questions: answer me directly.
    private const uint Lifetime = 600;
    private const int MaxSuffix = 9;

    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), Port);
    private static readonly TimeSpan ProbeTime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ReannounceInterval = TimeSpan.FromSeconds(30);

    private readonly Socket _socket;
    private readonly IPAddress _address;
    private readonly CancellationTokenSource _stop = new();
    private string _name = "";
    private volatile bool _claimed;
    private volatile bool _conflict;
    private bool _disposed;

    private MdnsResponder(Socket socket, IPAddress address)
    {
        _socket = socket;
        _address = address;
    }

    /// <summary>The name answered to, "mynotch.local".</summary>
    public string Name => _name;

    /// <summary>
    /// Claims <c>{baseName}.local</c> (or the first free variant) for <paramref name="address"/>, on its interface.
    /// <c>null</c> if mDNS cannot be used here (port refused, no multicast).
    /// </summary>
    public static async Task<MdnsResponder?> StartAsync(IPAddress address, string baseName, CancellationToken cancellation)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
            try
            {
                socket.IOControl((IOControlCode)SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, address));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[mDNS] Not available: {ex.Message}");
            return null;
        }

        var responder = new MdnsResponder(socket, address);
        _ = responder.ListenAsync();
        try
        {
            for (int suffix = 1; suffix <= MaxSuffix; suffix++)
            {
                responder._name = suffix == 1 ? $"{baseName}.local" : $"{baseName}-{suffix}.local";
                if (await responder.ProbeAsync(cancellation).ConfigureAwait(false))
                {
                    responder._claimed = true;
                    _ = responder.AnnounceAsync();
                    return responder;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Debug.WriteLine($"[mDNS] Not claimed: {ex.Message}");
        }

        responder.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_claimed)
        {
            // Goodbye: a zero lifetime makes everyone forget the name now.
            TrySend(Answer(0, legacy: false, lifetime: 0), Group);
        }

        _stop.Cancel();
        _socket.Dispose();
    }

    /// <summary>Asks the network whether someone answers to the name already; <c>true</c> if nobody does.</summary>
    private async Task<bool> ProbeAsync(CancellationToken cancellation)
    {
        _conflict = false;
        var query = Query(_name);
        for (int i = 0; i < 3 && !_conflict; i++)
        {
            await _socket.SendToAsync(query, SocketFlags.None, Group, cancellation).ConfigureAwait(false);
            await Task.Delay(ProbeTime / 3, cancellation).ConfigureAwait(false);
        }

        return !_conflict;
    }

    /// <summary>Tells the network the name now leads here: twice at start, then periodically to keep Wi-Fi caches fresh.</summary>
    private async Task AnnounceAsync()
    {
        try
        {
            for (int i = 0; i < 2 && !_stop.IsCancellationRequested; i++)
            {
                await _socket.SendToAsync(Answer(0, legacy: false, Lifetime), SocketFlags.None, Group, _stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            }

            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(ReannounceInterval, _stop.Token).ConfigureAwait(false);
                TrySend(Answer(0, legacy: false, Lifetime), Group);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Stopped meanwhile.
        }
    }

    private async Task ListenAsync()
    {
        var buffer = new byte[9000];
        var anywhere = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested && !_disposed)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anywhere, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (!_stop.IsCancellationRequested && !_disposed)
            {
                continue; // An ICMP unreachable or transient network error must never stop the responder.
            }
            catch (SocketException)
            {
                return;
            }

            try
            {
                Handle(buffer.AsSpan(0, received.ReceivedBytes), (IPEndPoint)received.RemoteEndPoint);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
            {
                // A malformed packet: not ours to fix.
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> packet, IPEndPoint sender)
    {
        if (packet.Length < 12)
        {
            return;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        bool response = (packet[2] & 0x80) != 0;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int offset = 12;

        if (response)
        {
            // While probing: someone else answering to the name (not this PC echoing itself) means it is taken.
            if (_claimed || sender.Address.Equals(_address))
            {
                return;
            }

            for (int i = 0; i < questions; i++)
            {
                ReadName(packet, ref offset);
                offset += 4;
            }

            for (int i = 0; i < answers; i++)
            {
                string name = ReadName(packet, ref offset);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
                offset += 10 + length;
                if (type == TypeA && name.Equals(_name, StringComparison.OrdinalIgnoreCase))
                {
                    _conflict = true;
                }
            }

            return;
        }

        if (!_claimed)
        {
            return;
        }

        for (int i = 0; i < questions; i++)
        {
            string name = ReadName(packet, ref offset);
            if (offset + 4 > packet.Length)
            {
                return;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            ushort klass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;
            if (!name.Equals(_name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool legacy = sender.Port != Port;
            if (type is not (TypeA or TypeAny))
            {
                // Answer non-IPv4 queries (like AAAA) with an immediate authoritative empty reply so resolvers don't stall.
                if (legacy)
                {
                    TrySend(NoDataAnswer(id, type), sender);
                }

                continue;
            }

            if (legacy)
            {
                TrySend(Answer(id, legacy: true, Lifetime), sender);
                TrySend(Answer(0, legacy: false, Lifetime), Group);
            }
            else if ((klass & UnicastResponse) != 0)
            {
                TrySend(Answer(0, legacy: false, Lifetime), sender);
            }
            else
            {
                TrySend(Answer(0, legacy: false, Lifetime), Group);
            }

            return;
        }
    }

    private void TrySend(byte[] packet, IPEndPoint to)
    {
        try
        {
            _socket.SendTo(packet, to);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Debug.WriteLine($"[mDNS] Not sent: {ex.Message}");
        }
    }

    /// <summary>"Who is <paramref name="name"/>?", asking for a direct answer.</summary>
    private static byte[] Query(string name)
    {
        var packet = new List<byte>(64);
        Write16(packet, 0); // id
        Write16(packet, 0); // flags: a standard query
        Write16(packet, 1); // one question
        Write16(packet, 0);
        Write16(packet, 0);
        Write16(packet, 0);
        WriteName(packet, name);
        Write16(packet, TypeAny);
        Write16(packet, (ushort)(UnicastResponse | ClassIn));
        return [.. packet];
    }

    /// <summary>"<see cref="Name"/> is this PC's address", for <paramref name="lifetime"/> seconds.</summary>
    private byte[] Answer(ushort id, bool legacy, uint lifetime)
    {
        var packet = new List<byte>(64);
        Write16(packet, id);
        Write16(packet, 0x8400); // A response, authoritative.
        Write16(packet, (ushort)(legacy ? 1 : 0)); // A plain DNS answer repeats the question.
        Write16(packet, 1);
        Write16(packet, 0);
        Write16(packet, 0);
        if (legacy)
        {
            WriteName(packet, _name);
            Write16(packet, TypeA);
            Write16(packet, ClassIn);
        }

        WriteName(packet, _name);
        Write16(packet, TypeA);
        Write16(packet, legacy ? ClassIn : (ushort)(CacheFlush | ClassIn));
        Write16(packet, (ushort)(lifetime >> 16));
        Write16(packet, (ushort)lifetime);
        Write16(packet, 4);
        packet.AddRange(_address.GetAddressBytes());
        return [.. packet];
    }

    private byte[] NoDataAnswer(ushort id, ushort queryType)
    {
        var packet = new List<byte>(48);
        Write16(packet, id);
        Write16(packet, 0x8400);
        Write16(packet, 1);
        Write16(packet, 0);
        Write16(packet, 0);
        Write16(packet, 0);
        WriteName(packet, _name);
        Write16(packet, queryType);
        Write16(packet, ClassIn);
        return [.. packet];
    }

    /// <summary>A name in a packet: its labels, or a pointer to them earlier in the packet.</summary>
    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var name = new StringBuilder();
        int position = offset;
        bool jumped = false;
        for (int jumps = 0; ; )
        {
            int length = packet[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (++jumps > 16)
                {
                    throw new FormatException("Pointer loop.");
                }

                int target = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = target;
                continue;
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }

        if (!jumped)
        {
            offset = position;
        }

        return name.ToString();
    }

    private static void WriteName(List<byte> packet, string name)
    {
        foreach (string label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }

        packet.Add(0);
    }

    private static void Write16(List<byte> packet, ushort value)
    {
        packet.Add((byte)(value >> 8));
        packet.Add((byte)value);
    }
}
