namespace zktecoKhaalt;

public class OdooConfig
{
    public string BaseUrl { get; set; } = "http://localhost:8069";
    public string Database { get; set; } = "odoo";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    public string ApiKey { get; set; } = "";
}
