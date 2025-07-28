using AdminShellNS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Threading.Tasks;
using Aas = AasCore.Aas3_0;

namespace AasxPluginAssetInterfaceDescription
{
    public class AidKnxiotConnection : AidBaseConnection
    {
        public BacnetClient Client1;
        private Dictionary<uint, BacnetAddress> DeviceAddresses = new Dictionary<uint, BacnetAddress>();
        public BacnetAddress deviceAddress;

        override public async Task<bool> Open()
        {
            try
            {
                Client1 = new BacnetClient();
                Client1.OnIam += OnIamHandler;

                if (TimeOutMs >= 10)
                {
                    Client1.Timeout = (int)TimeOutMs;
                }

                Client1.Start();

                // Extract device ID from the URI
                uint deviceId = uint.Parse(TargetUri.Host);
                if (!DeviceAddresses.ContainsKey(deviceId))
                {
                    Client1.WhoIs((int)deviceId, (int)deviceId);
                    await Task.Delay(1000);
                }
                if (!DeviceAddresses.TryGetValue(deviceId, out deviceAddress))
                {
                    return false;
                }

                await Task.Yield();
                return true;
            }
            catch (Exception)
            {
                Client1 = null;
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
            return Client1 != null;
        }

        override public void Close()
        {
            // Dispose client
            if (Client1 != null)
            {
                Client1.Dispose();
                Client1 = null;
            }
        }

        override public int UpdateItemValue(AidIfxItemStatus item)
        {
            int res = 0;
            if (item?.FormData?.Href?.HasContent() != true ||
                item.FormData.Bacv_useService?.HasContent() != true ||
                !IsConnected() ||
                Client1 == null)
            {
                return res;
            }
            try
            {

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
                        IList<BacnetValue> values_r1 = new List<BacnetValue>();
                        bool result_r1 = Client1.ReadPropertyRequest(deviceAddress, objectId, propertyId, out values_r1);
                        if (result_r1 && values_r1.Count > 0 && values_r1[0].Value != null)
                        {
                            float val_r1 = (float)values_r1[0].Value;
                            item.Value = val_r1.ToString("R", CultureInfo.InvariantCulture);
                            NotifyOutputItems(item, item.Value);
                            res = 1;
                        }
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
                                        IList<BacnetValue> values_r2 = new List<BacnetValue>();
                                        bool result_r2 = Client1.ReadPropertyRequest(deviceAddress, objectId, propertyId, out values_r2);
                                        if (result_r2 && values_r2.Count > 0 && values_r2[0].Value != null && prop.Value == item.Value)
                                        {
                                            float val_r2 = (float)values_r2[0].Value;
                                            item.Value = val_r2.ToString("R", CultureInfo.InvariantCulture);
                                            NotifyOutputItems(item, item.Value);
                                            res = 1;
                                        }
                                    }
                                    else
                                    {
                                        float staticValue = float.Parse(prop.Value, CultureInfo.InvariantCulture);
                                        BacnetValue[] values_w = new BacnetValue[] { new BacnetValue(staticValue) };
                                        bool result_w = Client1.WritePropertyRequest(deviceAddress, objectId, propertyId, values_w);
                                        if (result_w)
                                        {
                                            float val_r3 = (float)values_w[0].Value;
                                            item.Value = val_r3.ToString("R", CultureInfo.InvariantCulture);
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