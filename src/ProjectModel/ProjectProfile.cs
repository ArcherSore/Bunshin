namespace ProjectModel;

// What belongs to the one project Bunshin currently serves and is not declared in its Project Interface. The
// mechanisms (running, observing, updating) never name the project; they read interface.json or come here (ADR 0002).
public static class ProjectProfile
{
    // Configuration files from earlier releases predate the project identity field.
    internal const string LegacyConfigurationProject = "MaaNOP";
    public const string SponsorUrl = "https://afdian.com/a/archersore";
}
