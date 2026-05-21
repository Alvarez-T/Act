using System;

namespace YFex.Messaging.Generator;

/// <summary>
/// Represents one class containing [Live] decorated methods.
/// One .Live.g.cs file is emitted per LiveClassModel.
/// </summary>
internal readonly struct LiveClassModel : IEquatable<LiveClassModel>
{
    public string Namespace { get; }
    public string ClassName { get; }

    /// <summary>
    /// True when the class inherits from MvvmStateObject.
    /// When true the emitter writes a GetPropertyChangedArgs override so
    /// INotifyPropertyChanged fires with the correct property name.
    /// </summary>
    public bool InheritsMvvmStateObject { get; }

    /// <summary>
    /// True when the class inherits from PageViewModel (YFex.Mvvm).
    /// When true the emitter writes OnSuspendCascading / OnResumeCascading overrides
    /// to implement LiveSuspendBehavior for each [Live] property.
    /// </summary>
    public bool InheritsPageViewModel { get; }

    public EquatableArray<LiveMethodModel> Methods { get; }

    public bool HasAnyLiveProperties => !Methods.IsEmpty;

    public LiveClassModel(
        string @namespace,
        string className,
        bool inheritsMvvmStateObject,
        bool inheritsPageViewModel,
        EquatableArray<LiveMethodModel> methods)
    {
        Namespace               = @namespace;
        ClassName               = className;
        InheritsMvvmStateObject = inheritsMvvmStateObject;
        InheritsPageViewModel   = inheritsPageViewModel;
        Methods                 = methods;
    }

    public bool Equals(LiveClassModel other) =>
        Namespace               == other.Namespace               &&
        ClassName               == other.ClassName               &&
        InheritsMvvmStateObject == other.InheritsMvvmStateObject &&
        InheritsPageViewModel   == other.InheritsPageViewModel   &&
        Methods                 == other.Methods;

    public override bool Equals(object? obj) => obj is LiveClassModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (Namespace?.GetHashCode() ?? 0);
            h = h * 31 + (ClassName?.GetHashCode() ?? 0);
            h = h * 31 + InheritsMvvmStateObject.GetHashCode();
            h = h * 31 + InheritsPageViewModel.GetHashCode();
            h = h * 31 + Methods.GetHashCode();
            return h;
        }
    }
}
