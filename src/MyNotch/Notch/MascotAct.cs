namespace MyNotch.Notch;

/// <summary>What a module has the mascot act out (the assistant's conversation, see <see cref="NotchContext.ActMascot"/>).</summary>
public enum MascotAct
{
    /// <summary>Back to itself.</summary>
    Rest,

    /// <summary>Listening: it looks at what is being typed.</summary>
    Listen,

    /// <summary>Thinking: eyes raised, dots orbiting it.</summary>
    Think,

    /// <summary>Talking: a small bounce at each word written.</summary>
    Talk,

    /// <summary>Done, and pleased: a wink.</summary>
    Done,

    /// <summary>It went wrong: sad, for a moment.</summary>
    Fail,
}
