using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using CoAP;
using CoAP.Net;
using AdminShellNS;
using AdminShellNS.DiaryData;
using AasxIntegrationBase;
using AasxPluginAssetInterfaceDescription;

namespace AasxPluginAssetInterfaceDescription
{
    public class AidKnxiotConnection : AidBaseConnection
    {
        public CoAPEndPoint Endpoint;
        public IPAddress Ipv6Address;
        public CancellationTokenSource mdnsCts;

        override public async Task<bool> Open()
        {
            Endpoint = new CoAPEndPoint();
            Endpoint.Start();

            mdnsCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeOutMs > 0 ? TimeOutMs : 1000));
            Ipv6Address = await ResolveMdnsAsync(TargetUri.Host, mdnsCts.Token);

            await Task.Yield();
            return true;    
        }

        override public bool IsConnected()
        {
            return Endpoint != null;
        }

        override public void Close()
        {
            mdnsCts?.Cancel();
            mdnsCts?.Dispose();
            mdnsCts = null;

            if (Endpoint != null)
            {
                Endpoint.Stop();
                Endpoint.Dispose();
                Endpoint = null;
            }
            Ipv6Address = null;
        }

        override public async Task<int> UpdateItemValueAsync(AidIfxItemStatus item)
        {
            if (item?.FormData?.Href?.HasContent() != true ||
                item?.FormData.Cov_method?.HasContent() != true ||
                !IsConnected())
                return 0;
            int res = 0;

            string coapPath = item.FormData.Href.TrimStart('/');
            string coapMethod = item.FormData.Cov_method.Trim().ToLower() ?? "get"; 
            
            if (coapMethod == "get")
            {
                try
                {
                    string result = await CoapGetAsync(Ipv6Address, coapPath, (int)TimeOutMs);
                    item.Value = result;
                    NotifyOutputItems(item, item.Value);
                    res = 1;

                }
                catch (Exception)
                {
                    return res;
                }
            }
            //else if (coapMethod == "put")
            //   {
            //       bool putSuccess = await CoapPutAsync(Ipv6Address, coapPath, item.Value, (int)TimeOutMs);
            //       if (putSuccess)
            //       {
            //           item.Value = item.Value;
            //           NotifyOutputItems(item, item.Value);
            //           res = 1;
            //       }
            //   }
            return res;
        }
         
        private async Task<string> CoapGetAsync(IPAddress ip, string path, int timeoutMs)
        {
            Uri uri = new Uri($"coap://[{ip}]/{path}");
            var request = new Request(Method.GET)
            {
                URI = uri,
                Type = MessageType.CON
            };
            request.EndPoint = Endpoint;

            var tcs = new TaskCompletionSource<Response>();
            request.Respond += (sender, args) =>
            {
                if (args.Response != null)
                    tcs.TrySetResult(args.Response);
            };

            request.Send();

            using (var cts = new CancellationTokenSource(timeoutMs))
            using (cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
            {
                try
                {
                    var response = await tcs.Task;
                    if (response.StatusCode == CoAP.StatusCode.Content)
                        return response.PayloadString;
                    else
                        throw new Exception($"CoAP GET failed with status code: {response.StatusCode} ({response.Code}) for {uri}");
                }
                catch (TaskCanceledException)
                {
                    throw new TimeoutException($"CoAP GET request to {uri} timed out after {timeoutMs}ms.");
                }
            }
        }
  
        //private async Task<bool> CoapPutAsync(IPAddress ip, string path, string payload, int timeoutMs, int contentFormat = 0)
        //{
        //    if (Endpoint == null || !Endpoint.Running)
        //        throw new InvalidOperationException("CoAP endpoint is not running. Call Open() first.");

        //    Uri uri = new Uri($"coap://[{ip}]:{CoAP.CoapConstants.DefaultPort}/{path}");
        //    var request = new Request(Method.PUT)
        //    {
        //        URI = uri,
        //        Type = MessageType.CON,
        //        Payload = System.Text.Encoding.UTF8.GetBytes(payload)
        //    };

        //    // Universal, works on both Makaretu.Dns.CoAP and CoAP.NET.Core
        //    request.Options.Add(new Option(CoAP.OptionType.ContentFormat, BitConverter.GetBytes((ushort)contentFormat)));
        //    // If the above fails, try: request.Options.Add(new Option(CoAP.OptionType.ContentFormat, contentFormat));

        //    request.EndPoint = Endpoint;

        //    var tcs = new TaskCompletionSource<Response>();
        //    request.Respond += (sender, args) =>
        //    {
        //        if (args.Response != null)
        //            tcs.TrySetResult(args.Response);
        //        else
        //            tcs.TrySetException(new Exception("Null CoAP response received."));
        //    };

        //    request.Send();

        //    using (var cts = new CancellationTokenSource(timeoutMs))
        //    using (cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
        //    {
        //        try
        //        {
        //            var response = await tcs.Task;
        //            if (response.StatusCode == CoAP.StatusCode.Changed)
        //                return true;
        //            else
        //                throw new Exception($"CoAP PUT failed with status code: {response.StatusCode} ({response.Code}) for {uri}");
        //        }
        //        catch (TaskCanceledException)
        //        {
        //            throw new TimeoutException($"CoAP PUT request to {uri} timed out after {timeoutMs}ms.");
        //        }
        //    }
        //}

        private async Task<IPAddress> ResolveMdnsAsync(string hostname, CancellationToken cancellationToken)
        {
            var serviceDiscovery = new MulticastService();
            var tcs = new TaskCompletionSource<IPAddress>();

            serviceDiscovery.AnswerReceived += (s, e) =>
            {
                var addressRecord = e.Message.Answers
                    .OfType<AddressRecord>()
                    .FirstOrDefault(r =>
                        r.Name.ToString().TrimEnd('.') == hostname.TrimEnd('.'));

                if (addressRecord != null)
                {
                    if (addressRecord.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        tcs.TrySetResult(addressRecord.Address);
                    else if (addressRecord.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (!tcs.Task.IsCompleted || tcs.Task.Result.AddressFamily != AddressFamily.InterNetworkV6)
                            tcs.TrySetResult(addressRecord.Address);
                    }
                }
            };

            serviceDiscovery.Start();

            try
            {
                var domainName = new DomainName(hostname);
                serviceDiscovery.SendQuery(domainName, DnsClass.IN, DnsType.AAAA);
                serviceDiscovery.SendQuery(domainName, DnsClass.IN, DnsType.A);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cancellationToken));
                if (completedTask == tcs.Task)
                    return tcs.Task.Result;
                else
                    cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            catch (OperationCanceledException) { throw; }
            finally { serviceDiscovery.Stop(); }
        }
    }
}