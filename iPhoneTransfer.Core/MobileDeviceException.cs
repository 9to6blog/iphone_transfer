using System;

namespace iPhoneTransfer.Core;

/// <summary>기기 통신 중 발생한 오류를 사용자에게 보여줄 메시지와 함께 감싼다.</summary>
public sealed class MobileDeviceException : Exception
{
    public MobileDeviceException(string message) : base(message) { }
    public MobileDeviceException(string message, Exception inner) : base(message, inner) { }
}
