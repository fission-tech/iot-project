using System.Net.Sockets;
using FluentModbus;

namespace EdgeGateway.Gateway.Acquisition;

internal static class ModbusErrors
{
    public static string Describe(ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "Device does not support the requested operation.",
        ModbusExceptionCode.IllegalDataAddress => "Device rejected the requested register range.",
        ModbusExceptionCode.IllegalDataValue => "Device rejected a request parameter or quantity.",
        ModbusExceptionCode.ServerDeviceFailure => "Device failed while processing the request.",
        ModbusExceptionCode.Acknowledge => "Device accepted the request but has not completed processing.",
        ModbusExceptionCode.ServerDeviceBusy => "Device is busy processing another operation.",
        ModbusExceptionCode.MemoryParityError => "Device reported a memory consistency error.",
        ModbusExceptionCode.GatewayPathUnavailable => "Modbus gateway could not establish a path to the target.",
        ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => "Modbus gateway received no response from the target device.",
        _ => "Device returned an unrecognised Modbus exception code."
    };

    public static string Describe(Exception error)
    {
        if (error is ModbusException modbus)
        {
            // FluentModbus uses 255 when there is no device exception code (e.g. malformed response).
            return (byte)modbus.ExceptionCode == byte.MaxValue
                ? $"InvalidResponse: {modbus.Message}"
                : $"{modbus.ExceptionCode} (0x{(int)modbus.ExceptionCode:X2}): {Describe(modbus.ExceptionCode)}";
        }
        var cause = error.GetBaseException();
        return $"{(cause is SocketException socket ? socket.SocketErrorCode.ToString() : cause.GetType().Name)}: {cause.Message}";
    }
}
