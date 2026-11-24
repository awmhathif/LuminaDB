using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LuminaDB.Core.Web
{
    /// <summary>This PC's address on the local network, for demo mode (SPEC §5.2).</summary>
    public sealed class LanAddress
    {
        public string Address { get; set; }
        public string AdapterName { get; set; }
        public bool HasGateway { get; set; }
        public override string ToString() => Address + " (" + AdapterName + ")";

        /// <summary>Usable IPv4 addresses, best first: private ranges on adapters with a gateway (Wi-Fi, Ethernet).</summary>
        public static List<LanAddress> Find()
        {
            var result = new List<LanAddress>();
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return result; }
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                // Virtual adapters (Hyper-V, VirtualBox, VMware, WSL) aren't reachable from a phone or a lecturer's laptop.
                bool isVirtual = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("vbox") || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("loopback");
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    var b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;   // no DHCP answer: unusable
                    if (isVirtual && !gateway) continue;
                    result.Add(new LanAddress { Address = ip.ToString(), AdapterName = nic.Name, HasGateway = gateway });
                }
            }
            return result.OrderByDescending(a => a.HasGateway).ThenByDescending(a => IsPrivate(a.Address)).ToList();
        }

        public static bool IsPrivate(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
    }
}
