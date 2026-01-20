using S7.Net;
using System;

public class PlcService
{
    private readonly Plc _plc;

    public PlcService(string ip, short rack, short slot)
    {
        _plc = new Plc(CpuType.S71200, ip, rack, slot);
    }

    public bool IsConnected => _plc?.IsConnected ?? false;

    public void Connect()
    {
        if (_plc == null) throw new InvalidOperationException("PLC object not initialized.");
        if (!_plc.IsConnected)
        {
            _plc.Open();
        }
    }

    public object Read(string address)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        return _plc.Read(address);
    }

    public void Write(string address, object value)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        _plc.Write(address, value);
    }
}
