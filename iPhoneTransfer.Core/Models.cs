namespace iPhoneTransfer.Core;

/// <summary>연결된 아이폰 한 대의 식별 정보.</summary>
public sealed record DeviceInfo(string Udid, string Name, bool IsReady = true, string? ConnectionError = null)
{
    public override string ToString() => $"{Name} ({Udid[..System.Math.Min(8, Udid.Length)]}…)";
}

/// <summary>기기 안의 사진/영상 한 개.</summary>
public sealed record PhotoItem(string DevicePath, string FileName, long Size, System.DateTime Modified)
{
    // 12시간제(오전/오후) 표기를 OS 언어와 무관하게 보장하기 위해 한국 문화권 고정.
    private static readonly System.Globalization.CultureInfo Ko =
        System.Globalization.CultureInfo.GetCultureInfo("ko-KR");

    public string SizeText => Size >= 1 << 20
        ? $"{Size / (1024.0 * 1024.0):0.0} MB"
        : $"{Size / 1024.0:0} KB";

    // 예: 2026-06-24 오후 6:15  (tt = 오전/오후, h = 12시간제)
    public string DateText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd tt h:mm", Ko);
}

/// <summary>파일 공유(File Sharing)를 지원하는 설치된 앱.</summary>
public sealed record SharingApp(string BundleId, string DisplayName)
{
    public override string ToString() => $"{DisplayName}  ·  {BundleId}";
}

/// <summary>전송 진행 상황 보고용.</summary>
public sealed record TransferProgress(int Index, int Total, string CurrentFile, long BytesDone = 0, long BytesTotal = 0)
{
    public double Percent => BytesTotal > 0
        ? (double)BytesDone / BytesTotal * 100.0
        : Total == 0 ? 0 : (double)Index / Total * 100.0;
}
