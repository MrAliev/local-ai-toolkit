namespace LocalLm.Core;

/// <summary>Log triage found no model it could use; the message states only what was established.</summary>
public sealed class NoTriageModelException(string message) : InvalidOperationException(message);
