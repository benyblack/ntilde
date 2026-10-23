using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Ntilde.Platform.Ssh.Interactions;

namespace Ntilde.ViewModels.Ssh;

public sealed class AuthPromptViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool CanRememberPassword { get; init; }
    public bool RememberPassword { get; set; }

    /// <summary>
    /// The fields answer a keyboard-interactive round, one answer per question even when it asks just one (PAM's
    /// "Password: "): the server counts the answers, and sshd drops the connection over a wrong count.
    /// </summary>
    public bool IsKeyboardInteractive { get; init; }

    public ObservableCollection<AuthPromptEntryViewModel> Prompts { get; } = new();

    public SshInteractionResponse BuildResponse()
    {
        if (Prompts.Count == 1 && !IsKeyboardInteractive)
        {
            return SshInteractionResponse.FromSecret(Prompts[0].Value, RememberPassword);
        }

        return SshInteractionResponse.FromKeyboardResponses(Prompts.Select(prompt => prompt.Value).ToArray());
    }
}

public sealed class AuthPromptEntryViewModel : INotifyPropertyChanged
{
    private string _value = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Prompt { get; init; } = string.Empty;
    public bool IsSecret { get; init; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    public string PasswordCharText => IsSecret ? "*" : string.Empty;
}
