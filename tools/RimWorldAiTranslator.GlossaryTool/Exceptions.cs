namespace RimWorldAiTranslator.GlossaryTool;

internal class GlossaryToolException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}

internal sealed class InputDataException(string message, Exception? innerException = null)
    : GlossaryToolException(message, innerException)
{
}
