namespace Heartbeat.Shared;

public static class Permissions
{
    public const string CrewView = "crew.view";
    public const string CrewManage = "crew.manage";
    public const string VitalsView = "vitals.view";
    public const string VitalsHistory = "vitals.history";
    public const string AlertsView = "alerts.view";
    public const string AlertsAcknowledge = "alerts.acknowledge";
    public const string MessagesView = "messages.view";
    public const string MessagesSend = "messages.send";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";

    public static readonly IReadOnlyList<string> All = new[]
    {
        CrewView,
        CrewManage,
        VitalsView,
        VitalsHistory,
        AlertsView,
        AlertsAcknowledge,
        MessagesView,
        MessagesSend,
        UsersView,
        UsersManage,
        RolesManage
    };
}

public static class RoleNames
{
    public const string Administrator = "Administrator";
    public const string FlightSurgeon = "Flight Surgeon";
    public const string MissionControlOperator = "Mission Control Operator";
}