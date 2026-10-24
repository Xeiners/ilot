using CommunityToolkit.Mvvm.ComponentModel;

namespace MyNotch.Modules.Study;

/// <summary>
/// A section of the courses (a subject: « Maths », « Physique »…), made by the user: its name, its colour (the tint of its
/// tab and of its courses' tiles), its place among the others. A course is in one section or none; « Global » (no section
/// chosen) shows them all.
/// </summary>
public sealed class StudySection(long id, string name, string color, int position) : ObservableObject
{
    private string _name = name;
    private string _color = color;

    public long Id { get; } = id;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>« #AARRGGBB ».</summary>
    public string Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    public int Position { get; set; } = position;
}
