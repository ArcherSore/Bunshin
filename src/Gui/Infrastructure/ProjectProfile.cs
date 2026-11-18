namespace Gui.Infrastructure;

// What belongs to the one project Bunshin currently serves and is not declared in its Project Interface. The
// mechanisms (running, observing, updating) never name the project; they read interface.json or come here (ADR 0002).
internal static class ProjectProfile
{
    internal const string SponsorUrl = "https://afdian.com/a/archersore";
}
