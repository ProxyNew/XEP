namespace XEnchantmentPatcher;

// A route or capacity refusal: native startup rules (SkyPatcher, LLOS) would keep a list from carrying the planned
// entries, or the writer cannot prove that they would not. Planning asks the writer's exact question and reports only
// this type as a withheld placement; any other failure below that question fails the operation. The message is the
// reason alone. Where a refusal aborts Preview or Generate, the engine reports it as the InvalidDataException it
// always was, with the note that no output was installed.
internal sealed class RouteRefusalException(string reason) : Exception(reason);
