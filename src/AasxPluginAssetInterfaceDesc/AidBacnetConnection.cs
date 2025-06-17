using AasxIntegrationBase;
using AasxIntegrationBase.AdminShellEvents;
using AasxPluginAssetInterfaceDescription;
using AasxPredefinedConcepts;
using AasxPredefinedConcepts.AssetInterfacesDescription;
using AdminShellNS;
using AdminShellNS.DiaryData;
using Extensions;
using FluentModbus;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Services.Description;
using Workstation.ServiceModel.Ua;
using Aas = AasCore.Aas3_0;

namespace AasxPluginAssetInterfaceDescription
{
    public class AidBacnetConnection : AidBaseConnection
    {
        public BacnetClient Client;
        private Dictionary<uint, BacnetAddress> DeviceAddresses = new Dictionary<uint, BacnetAddress>();
        override public async Task<bool> Open()
        {
            try
            {
                Client = new BacnetClient();
                Client.OnIam += OnIamHandler;

                if (TimeOutMs >= 10)
                { 
                    Client.Timeout = (int)TimeOutMs;
                } 

                Client.Start();
                
                await Task.Yield();
                return true;
            }
            catch (Exception ex)
            {
                Client = null;
                return false;
            }
        }

        private void OnIamHandler(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxAPDU, BacnetSegmentations segmentation, ushort vendorId)
        {
            // Store the device address from I-Am response
            DeviceAddresses[deviceId] = adr;
        }

        override public bool IsConnected()
        {
            // nothing to do, this simple bacnet connection is stateless
            return Client != null;
        }

        override public void Close()
        {
            // Dispose client
            if (Client != null)
            {
                Client.Dispose();
                Client = null;
            }
        }

        override public async Task<int> UpdateItemValueAsync(AidIfxItemStatus item)
        {
            var items = new List<AidIfxItemStatus> { item }; // Add your items to this list

            int res = 0;

            // Iterate over each item to update
            foreach (var itm in items)
            {
                // Ensure that necessary references are present
                if (itm?.FormData?.Href?.HasContent() != true ||
                    itm.FormData.Bacv_useService?.HasContent() != true ||
                    !IsConnected() ||
                    Client == null)
                {
                    continue;
                }

                try
                {
                    // Extract device ID from the URI or other source based on your implementation
                    uint deviceId = uint.Parse(TargetUri.Host);

                    BacnetAddress deviceAddress;
                    if (!DeviceAddresses.ContainsKey(deviceId))
                    {
                        Client.WhoIs((int)deviceId, (int)deviceId);
                        await Task.Delay(1000);
                    }

                    if (!DeviceAddresses.TryGetValue(deviceId, out deviceAddress))
                    {
                        continue; // Skip this item if address not found
                    }

                    var href = itm.FormData.Href.TrimStart('/');
                    string[] mainParts = href.Split('/');
                    string[] objectParts = mainParts[0].Split(',');

                    var objectType = (BacnetObjectTypes)int.Parse(objectParts[0]);
                    uint instance = uint.Parse(objectParts[1]);
                    BacnetObjectId objectId = new BacnetObjectId(objectType, instance);
                    var propertyId = (BacnetPropertyIds)int.Parse(mainParts[1]);

                    // READ operation
                    if (itm.FormData.Bacv_useService.Trim().ToLower() == "readproperty")
                    {
                        try
                        {
                            IList<BacnetValue> values = new List<BacnetValue>();
                            bool result = Client.ReadPropertyRequest(deviceAddress, objectId, propertyId, out values);

                            if (result && values.Count > 0 && values[0].Value != null)
                            {
                                itm.Value = values[0].Value.ToString();
                                NotifyOutputItems(itm, itm.Value);
                                res++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception during read: {ex.Message}");
                        }
                    }
                    // WRITE operation
                    else if (itm.FormData.Bacv_useService.Trim().ToLower() == "writeproperty")
                    {
                        try
                        {
                            if (itm.MapOutputItems != null)
                                foreach (var moi in itm.MapOutputItems)
                                {
                                    // valid?
                                    if (moi?.MapRelation?.Second == null)
                                        continue;

                                    // For literal payloads
                                    else if (moi.MapRelation.SecondHint is Aas.Property prop)
                                    {
                                        // set here
                                        float staticValue = float.Parse(prop.Value);
                                        BacnetValue[] writeValue = new BacnetValue[] { new BacnetValue(staticValue) };
                                        bool result = Client.WritePropertyRequest(deviceAddress, objectId, propertyId, writeValue);
                                        if (result)
                                        {
                                            itm.Value = writeValue[0].Value?.ToString();
                                            NotifyOutputItems(itm, itm.Value);
                                            res++;
                                        }
                                    }
                                }
                            //float staticValue = float.Parse(itm.Value);
                            //BacnetValue[] writeValue = new BacnetValue[] { new BacnetValue(staticValue) };
                            //bool result = Client.WritePropertyRequest(deviceAddress, objectId, propertyId, writeValue);
                            //if (result)
                            //{
                                //itm.Value = staticValue.ToString();
                                //NotifyOutputItems(itm, itm.Value);
                               // res++;
                            //}

                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception during write: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"General exception: {ex.Message}");
                }
            }

            return res;
        }
    }
}