using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Networking
{
    public class NetworkManager : INetworkManager
    {
        private readonly ILogger _logger;

        private IPAddress[] _localIpAddresses;
        private readonly object _localIpAddressSyncLock = new object();

        public NetworkManager(ILogger<NetworkManager> logger)
        {
            _logger = logger;

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        public Func<string[]> LocalSubnetsFn { get; set; }

        public event EventHandler NetworkChanged;

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            _logger.LogDebug("NetworkAvailabilityChanged");
            OnNetworkChanged();
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            _logger.LogDebug("NetworkAddressChanged");
            OnNetworkChanged();
        }

        private void OnNetworkChanged()
        {
            lock (_localIpAddressSyncLock)
            {
                _localIpAddresses = null;
                _macAddresses = null;
            }

            if (NetworkChanged != null)
            {
                NetworkChanged(this, EventArgs.Empty);
            }
        }

        public IPAddress[] GetLocalIpAddresses(bool ignoreVirtualInterface = true)
        {
            lock (_localIpAddressSyncLock)
            {
                if (_localIpAddresses == null)
                {
                    var addresses = GetLocalIpAddressesInternal(ignoreVirtualInterface).ToArray();

                    _localIpAddresses = addresses;
                }

                return _localIpAddresses;
            }
        }

        private List<IPAddress> GetLocalIpAddressesInternal(bool ignoreVirtualInterface)
        {
            var list = GetIPsDefault(ignoreVirtualInterface).ToList();

            if (list.Count == 0)
            {
                list = GetLocalIpAddressesFallback().GetAwaiter().GetResult().ToList();
            }

            var listClone = list.ToList();

            return list
                .OrderBy(i => i.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(i => listClone.IndexOf(i))
                .Where(FilterIpAddress)
                .GroupBy(i => i.ToString())
                .Select(x => x.First())
                .ToList();
        }

        private static bool FilterIpAddress(IPAddress address)
        {
            if (address.IsIPv6LinkLocal
                || address.ToString().StartsWith("169.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public bool IsInPrivateAddressSpace(string endpoint)
        {
            return IsInPrivateAddressSpace(endpoint, true);
        }

        private bool IsInPrivateAddressSpace(string endpoint, bool checkSubnets)
        {
            if (string.Equals(endpoint, "::1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // ipv6
            if (endpoint.Split('.').Length > 4)
            {
                // Handle ipv4 mapped to ipv6
                var originalEndpoint = endpoint;
                endpoint = endpoint.Replace("::ffff:", string.Empty);

                if (string.Equals(endpoint, originalEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Private address space:
            // http://en.wikipedia.org/wiki/Private_network

            if (endpoint.StartsWith("172.", StringComparison.OrdinalIgnoreCase))
            {
                return Is172AddressPrivate(endpoint);
            }

            if (endpoint.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                endpoint.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
                endpoint.StartsWith("169.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (checkSubnets && endpoint.StartsWith("192.168", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (checkSubnets && IsInPrivateAddressSpaceAndLocalSubnet(endpoint))
            {
                return true;
            }

            return false;
        }

        public bool IsInPrivateAddressSpaceAndLocalSubnet(string endpoint)
        {
            if (endpoint.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
            {
                var endpointFirstPart = endpoint.Split('.')[0];

                var subnets = GetSubnets(endpointFirstPart);

                foreach (var subnet_Match in subnets)
                {
                    //logger.LogDebug("subnet_Match:" + subnet_Match);

                    if (endpoint.StartsWith(subnet_Match + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private Dictionary<string, List<string>> _subnetLookup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private List<string> GetSubnets(string endpointFirstPart)
        {
            lock (_subnetLookup)
            {
                if (_subnetLookup.TryGetValue(endpointFirstPart, out var subnets))
                {
                    return subnets;
                }

                subnets = new List<string>();

                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var unicastIPAddressInformation in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicastIPAddressInformation.Address.AddressFamily == AddressFamily.InterNetwork && endpointFirstPart == unicastIPAddressInformation.Address.ToString().Split('.')[0])
                        {
                            int subnet_Test = 0;
                            foreach (string part in unicastIPAddressInformation.IPv4Mask.ToString().Split('.'))
                            {
                                if (part.Equals("0")) break;
                                subnet_Test++;
                            }

                            var subnet_Match = string.Join(".", unicastIPAddressInformation.Address.ToString().Split('.').Take(subnet_Test).ToArray());

                            // TODO: Is this check necessary?
                            if (adapter.OperationalStatus == OperationalStatus.Up)
                            {
                                subnets.Add(subnet_Match);
                            }
                        }
                    }
                }

                _subnetLookup[endpointFirstPart] = subnets;

                return subnets;
            }
        }

        private static bool Is172AddressPrivate(string endpoint)
        {
            for (var i = 16; i <= 31; i++)
            {
                if (endpoint.StartsWith("172." + i.ToString(CultureInfo.InvariantCulture) + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsInLocalNetwork(string endpoint)
        {
            return IsInLocalNetworkInternal(endpoint, true);
        }

        public bool IsAddressInSubnets(string addressString, string[] subnets)
        {
            return IsAddressInSubnets(IPAddress.Parse(addressString), addressString, subnets);
        }

        private static bool IsAddressInSubnets(IPAddress address, string addressString, string[] subnets)
        {
            foreach (var subnet in subnets)
            {
                var normalizedSubnet = subnet.Trim();

                if (string.Equals(normalizedSubnet, addressString, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (normalizedSubnet.IndexOf('/') != -1)
                {
                    var ipnetwork = IPNetwork.Parse(normalizedSubnet);
                    if (ipnetwork.Contains(address))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInLocalNetworkInternal(string endpoint, bool resolveHost)
        {
            if (string.IsNullOrEmpty(endpoint))
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (IPAddress.TryParse(endpoint, out var address))
            {
                var addressString = address.ToString();

                var localSubnetsFn = LocalSubnetsFn;
                if (localSubnetsFn != null)
                {
                    var localSubnets = localSubnetsFn();
                    foreach (var subnet in localSubnets)
                    {
                        // only validate if there's at least one valid entry
                        if (!string.IsNullOrWhiteSpace(subnet))
                        {
                            return IsAddressInSubnets(address, addressString, localSubnets) || IsInPrivateAddressSpace(addressString, false);
                        }
                    }
                }

                int lengthMatch = 100;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    lengthMatch = 4;
                    if (IsInPrivateAddressSpace(addressString, true))
                    {
                        return true;
                    }
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    lengthMatch = 9;
                    if (IsInPrivateAddressSpace(endpoint, true))
                    {
                        return true;
                    }
                }

                // Should be even be doing this with ipv6?
                if (addressString.Length >= lengthMatch)
                {
                    var prefix = addressString.Substring(0, lengthMatch);

                    if (GetLocalIpAddresses().Any(i => i.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            else if (resolveHost)
            {
                if (Uri.TryCreate(endpoint, UriKind.RelativeOrAbsolute, out var uri))
                {
                    try
                    {
                        var host = uri.DnsSafeHost;
                        _logger.LogDebug("Resolving host {0}", host);

                        address = GetIpAddresses(host).Result.FirstOrDefault();

                        if (address != null)
                        {
                            _logger.LogDebug("{0} resolved to {1}", host, address);

                            return IsInLocalNetworkInternal(address.ToString(), false);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Can happen with reverse proxy or IIS url rewriting
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error resolving hostname");
                    }
                }
            }

            return false;
        }

        private static Task<IPAddress[]> GetIpAddresses(string hostName)
        {
            return Dns.GetHostAddressesAsync(hostName);
        }

        private IEnumerable<IPAddress> GetIPsDefault(bool ignoreVirtualInterface)
        {
            IEnumerable<NetworkInterface> interfaces;

            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == OperationalStatus.Up
                        || x.OperationalStatus == OperationalStatus.Unknown);
            }
            catch (NetworkInformationException ex)
            {
                _logger.LogError(ex, "Error in GetAllNetworkInterfaces");
                return Enumerable.Empty<IPAddress>();
            }

            return interfaces.SelectMany(network =>
            {
                var ipProperties = network.GetIPProperties();

                // Try to exclude virtual adapters
                // http://stackoverflow.com/questions/8089685/c-sharp-finding-my-machines-local-ip-address-and-not-the-vms
                var addr = ipProperties.GatewayAddresses.FirstOrDefault();
                if (addr == null
                    || (ignoreVirtualInterface
                        && (addr.Address.Equals(IPAddress.Any) || addr.Address.Equals(IPAddress.IPv6Any))))
                {
                    return Enumerable.Empty<IPAddress>();
                }

                return ipProperties.UnicastAddresses
                    .Select(i => i.Address)
                    .Where(i => i.AddressFamily == AddressFamily.InterNetwork || i.AddressFamily == AddressFamily.InterNetworkV6);
            }).GroupBy(i => i.ToString())
                .Select(x => x.First());
        }

        private static async Task<IEnumerable<IPAddress>> GetLocalIpAddressesFallback()
        {
            var host = await Dns.GetHostEntryAsync(Dns.GetHostName()).ConfigureAwait(false);

            // Reverse them because the last one is usually the correct one
            // It's not fool-proof so ultimately the consumer will have to examine them and decide
            return host.AddressList
                .Where(i => i.AddressFamily == AddressFamily.InterNetwork || i.AddressFamily == AddressFamily.InterNetworkV6)
                .Reverse();
        }

        /// <summary>
        /// Gets a random port number that is currently available
        /// </summary>
        /// <returns>System.Int32.</returns>
        public int GetRandomUnusedTcpPort()
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public int GetRandomUnusedUdpPort()
        {
            var localEndPoint = new IPEndPoint(IPAddress.Any, 0);
            using (var udpClient = new UdpClient(localEndPoint))
            {
                var port = ((IPEndPoint)(udpClient.Client.LocalEndPoint)).Port;
                return port;
            }
        }

        private List<string> _macAddresses;
        public List<string> GetMacAddresses()
        {
            if (_macAddresses == null)
            {
                _macAddresses = GetMacAddressesInternal();
            }
            return _macAddresses;
        }

        private static List<string> GetMacAddressesInternal()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(i =>
                {
                    try
                    {
                        var physicalAddress = i.GetPhysicalAddress();

                        if (physicalAddress == null)
                        {
                            return null;
                        }

                        return physicalAddress.ToString();
                    }
                    catch (Exception)
                    {
                        //TODO Log exception.
                        return null;
                    }
                })
                .Where(i => i != null)
                .ToList();
        }

        /// <summary>
        /// Parses the specified endpointstring.
        /// </summary>
        /// <param name="endpointstring">The endpointstring.</param>
        /// <returns>IPEndPoint.</returns>
        public IPEndPoint Parse(string endpointstring)
        {
            return Parse(endpointstring, -1).Result;
        }

        /// <summary>
        /// Parses the specified endpointstring.
        /// </summary>
        /// <param name="endpointstring">The endpointstring.</param>
        /// <param name="defaultport">The defaultport.</param>
        /// <returns>IPEndPoint.</returns>
        /// <exception cref="ArgumentException">Endpoint descriptor may not be empty.</exception>
        /// <exception cref="FormatException"></exception>
        private static async Task<IPEndPoint> Parse(string endpointstring, int defaultport)
        {
            if (string.IsNullOrEmpty(endpointstring)
                || endpointstring.Trim().Length == 0)
            {
                throw new ArgumentException("Endpoint descriptor may not be empty.");
            }

            if (defaultport != -1 &&
                (defaultport < IPEndPoint.MinPort
                || defaultport > IPEndPoint.MaxPort))
            {
                throw new ArgumentException(string.Format("Invalid default port '{0}'", defaultport));
            }

            string[] values = endpointstring.Split(new char[] { ':' });
            IPAddress ipaddy;
            int port = -1;

            //check if we have an IPv6 or ports
            if (values.Length <= 2) // ipv4 or hostname
            {
                port = values.Length == 1 ? defaultport : GetPort(values[1]);

                //try to use the address as IPv4, otherwise get hostname
                if (!IPAddress.TryParse(values[0], out ipaddy))
                    ipaddy = await GetIPfromHost(values[0]).ConfigureAwait(false);
            }
            else if (values.Length > 2) //ipv6
            {
                //could [a:b:c]:d
                if (values[0].StartsWith("[") && values[values.Length - 2].EndsWith("]"))
                {
                    string ipaddressstring = string.Join(":", values.Take(values.Length - 1).ToArray());
                    ipaddy = IPAddress.Parse(ipaddressstring);
                    port = GetPort(values[values.Length - 1]);
                }
                else //[a:b:c] or a:b:c
                {
                    ipaddy = IPAddress.Parse(endpointstring);
                    port = defaultport;
                }
            }
            else
            {
                throw new FormatException(string.Format("Invalid endpoint ipaddress '{0}'", endpointstring));
            }

            if (port == -1)
                throw new ArgumentException(string.Format("No port specified: '{0}'", endpointstring));

            return new IPEndPoint(ipaddy, port);
        }

        protected static readonly CultureInfo UsCulture = new CultureInfo("en-US");

        /// <summary>
        /// Gets the port.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns>System.Int32.</returns>
        /// <exception cref="FormatException"></exception>
        private static int GetPort(string p)
        {
            if (!int.TryParse(p, out var port)
             || port < IPEndPoint.MinPort
             || port > IPEndPoint.MaxPort)
            {
                throw new FormatException(string.Format("Invalid end point port '{0}'", p));
            }

            return port;
        }

        /// <summary>
        /// Gets the I pfrom host.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns>IPAddress.</returns>
        /// <exception cref="ArgumentException"></exception>
        private static async Task<IPAddress> GetIPfromHost(string p)
        {
            var hosts = await Dns.GetHostAddressesAsync(p).ConfigureAwait(false);

            if (hosts == null || hosts.Length == 0)
                throw new ArgumentException(string.Format("Host not found: {0}", p));

            return hosts[0];
        }

        public bool IsInSameSubnet(IPAddress address1, IPAddress address2, IPAddress subnetMask)
        {
             IPAddress network1 = GetNetworkAddress(address1, subnetMask);
             IPAddress network2 = GetNetworkAddress(address2, subnetMask);
             return network1.Equals(network2);
        }

        private IPAddress GetNetworkAddress(IPAddress address, IPAddress subnetMask)
        {
            byte[] ipAdressBytes = address.GetAddressBytes();
            byte[] subnetMaskBytes = subnetMask.GetAddressBytes();

            if (ipAdressBytes.Length != subnetMaskBytes.Length)
            {
                throw new ArgumentException("Lengths of IP address and subnet mask do not match.");
            }

            byte[] broadcastAddress = new byte[ipAdressBytes.Length];
            for (int i = 0; i < broadcastAddress.Length; i++)
            {
                broadcastAddress[i] = (byte)(ipAdressBytes[i] & (subnetMaskBytes[i]));
            }

            return new IPAddress(broadcastAddress);
        }

        public IPAddress GetLocalIpSubnetMask(IPAddress address)
        {
            NetworkInterface[] interfaces;

            try
            {
                var validStatuses = new[] { OperationalStatus.Up, OperationalStatus.Unknown };

                interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => validStatuses.Contains(i.OperationalStatus))
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetAllNetworkInterfaces");
                return null;
            }

            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.GetIPProperties().GatewayAddresses.FirstOrDefault() != null)
                {
                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.Equals(address) && ip.IPv4Mask != null)
                        {
                           return ip.IPv4Mask;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the network shares.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>IEnumerable{NetworkShare}.</returns>
        public virtual IEnumerable<NetworkShare> GetNetworkShares(string path)
        {
            return new List<NetworkShare>();
        }

        /// <summary>
        /// Gets available devices within the domain
        /// </summary>
        /// <returns>PC's in the Domain</returns>
        public virtual IEnumerable<FileSystemEntryInfo> GetNetworkDevices()
        {
            return new List<FileSystemEntryInfo>();
        }
    }
}
