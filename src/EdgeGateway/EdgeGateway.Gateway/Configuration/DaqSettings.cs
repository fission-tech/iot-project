namespace EdgeGateway.Gateway.Configuration;

public sealed class DaqSettings
{
    public string ConfigUrl { get; set; } = "http://127.0.0.1:5300";
    public int ConfigRefreshSeconds { get; set; } = 10;
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "state", "daq");
    public int FailedPollsBeforeOffline { get; set; } = 3;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
