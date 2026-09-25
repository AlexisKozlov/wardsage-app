namespace RealmForge.Bridge.Actions;

/// <summary>An expected failure of a command (the player cancelled, the app is not running...): the command gets
/// the message, the log gets one line instead of a stack trace.</summary>
public sealed class ActionFailedException(string message) : Exception(message);
