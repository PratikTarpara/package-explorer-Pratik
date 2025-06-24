using AdminShellNS;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Threading.Tasks;
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
            catch (Exception)
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
            int res = 0;
            if (item?.FormData?.Href?.HasContent() != true || 
                item.FormData.Bacv_useService?.HasContent() != true || 
                !IsConnected() || 
                Client == null)
            {
                return res; 
            }
            try
            {
                // Extract device ID from the URI
                uint deviceId = uint.Parse(TargetUri.Host);
                
                BacnetAddress deviceAddress;
                if (!DeviceAddresses.ContainsKey(deviceId))
                {
                    Client.WhoIs((int)deviceId, (int)deviceId);
                    await Task.Delay(1000);
                }
                if (!DeviceAddresses.TryGetValue(deviceId, out deviceAddress))
                {
                    return res;
                }
                
                var href = item.FormData.Href.TrimStart('/');
                string[] mainParts = href.Split('/');
                string[] objectParts = mainParts[0].Split(',');

                var objectType = (BacnetObjectTypes)int.Parse(objectParts[0]);
                uint instance = uint.Parse(objectParts[1]);
                BacnetObjectId objectId = new BacnetObjectId(objectType, instance);
                    
                var propertyId = (BacnetPropertyIds)int.Parse(mainParts[1]);

                // READ operation
                if (item.FormData.Bacv_useService.Trim().ToLower() == "readproperty")
                {
                    try
                    {
                        IList<BacnetValue> values = new List<BacnetValue>();
                        bool result = Client.ReadPropertyRequest(deviceAddress, objectId, propertyId, out values);
                        
                        if (result && values.Count > 0 && values[0].Value != null)
                        {
                            item.Value = values[0].Value.ToString();
                            NotifyOutputItems(item, item.Value);
                            res = 1;
                        }
                        values.Clear();
                    }
                    catch (Exception)
                    {
                        return res;
                    }
                }

                // WRITE operation
                else if (item.FormData.Bacv_useService.Trim().ToLower() == "writeproperty")
                {
                    try
                    {
                        if (item.MapOutputItems != null)
                            foreach (var moi in item.MapOutputItems)
                            {
                                // valid?
                                if (moi?.MapRelation?.Second == null)
                                    continue;

                                // For literal payloads
                                else if (moi.MapRelation.SecondHint is Aas.Property prop)
                                {
                                    if (item.Value == "" || prop.Value == item.Value)
                                    {
                                        IList<BacnetValue> values = new List<BacnetValue>();
                                        bool result_R = Client.ReadPropertyRequest(deviceAddress, objectId, propertyId, out values);
                                        if (result_R && values.Count > 0 && values[0].Value != null && prop.Value == item.Value)
                                        {
                                            item.Value = values[0].Value.ToString();
                                            NotifyOutputItems(item, item.Value);
                                            res = 1;
                                        }
                                        values.Clear();
                                    }
                                    else
                                    {
                                        float staticValue = float.Parse(prop.Value);
                                        BacnetValue[] writeValue = new BacnetValue[] { new BacnetValue(staticValue) };
                                        bool result_W = Client.WritePropertyRequest(deviceAddress, objectId, propertyId, writeValue);
                                        if (result_W)
                                        {
                                            item.Value = writeValue[0].Value?.ToString();
                                            NotifyOutputItems(item, item.Value);
                                            res = 1;
                                        }
                                    }
                                }
                            }
                    }
                    catch (Exception)
                    {
                        return res;
                    }
                }                    
            }
            catch (Exception) 
            {
                return res;
            }
            return res;
        }                 
    }
}