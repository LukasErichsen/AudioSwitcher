using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HeadphoneSwitcher
{
    internal sealed class WindowsAudioBackend : IAudioBackend
    {
        public List<MMDevice> List(EDataFlow flow)
        {
            var devices = new List<MMDevice>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try { Marshal.ThrowExceptionForHR(collection.Item(i, out device)); devices.Add(Read(device)); }
                    finally { Release(device); }
                }
                devices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                return devices;
            }
            finally { Release(collection); Release(enumerator); }
        }
        public MMDevice Default(EDataFlow flow, ERole role)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
                if (hr == unchecked((int)0x80070490)) return null;
                Marshal.ThrowExceptionForHR(hr);
                return Read(device);
            }
            finally { Release(device); Release(enumerator); }
        }
        public void Set(string id, ERole role)
        {
            IPolicyConfig policy = null;
            try { policy = (IPolicyConfig)new PolicyConfigClientComObject(); Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role)); }
            finally { Release(policy); }
        }
        private static MMDevice Read(IMMDevice device)
        {
            string id;
            Marshal.ThrowExceptionForHR(device.GetId(out id));
            IPropertyStore properties = null;
            PROPVARIANT value = new PROPVARIANT();
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out properties));
                var key = PropertyKeys.DeviceFriendlyName;
                Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
                return new MMDevice(id, value.GetValue() ?? id);
            }
            finally { value.Clear(); Release(properties); }
        }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    }
    internal static class PropertyKeys
    {
        public static PROPERTYKEY DeviceFriendlyName
        {
            get { return new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; }
        }
    }

    internal sealed class MMDevice
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public bool Available = true;

        public MMDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString()
        {
            return Name + (Available ? "" : " - Disconnected");
        }
    }

    [Flags]
    internal enum DeviceState : uint
    {
        Active = 0x00000001
    }

    internal enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
        EDataFlow_enum_count
    }

    internal enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
        ERole_enum_count
    }

    internal enum StorageAccessMode : uint
    {
        Read = 0x00000000
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)]
        private ushort valueType;

        [FieldOffset(8)]
        private IntPtr pointerValue;

        public string GetValue()
        {
            if (valueType != 31)
            {
                throw new InvalidOperationException("Unsupported PROPVARIANT type: " + valueType);
            }

            return Marshal.PtrToStringUni(pointerValue);
        }

        public void Clear()
        {
            PropVariantClear(ref this);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT value);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClientComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int clsContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig] int OpenPropertyStore(StorageAccessMode storageAccessMode, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int Unused1();
        [PreserveSig] int Unused2();
        [PreserveSig] int Unused3();
        [PreserveSig] int Unused4();
        [PreserveSig] int Unused5();
        [PreserveSig] int Unused6();
        [PreserveSig] int Unused7();
        [PreserveSig] int Unused8();
        [PreserveSig] int Unused9();
        [PreserveSig] int Unused10();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole role);
        [PreserveSig] int Unused11();
    }
}

