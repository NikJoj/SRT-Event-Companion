using System.Windows;
using System.Windows.Input;
using Srt.EventCompanion.Core;

namespace Srt.EventCompanion.Desktop;

public partial class PairRigWindow : Window
{
    public PairRigWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CodeInput.Focus();
    }

    private void CodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PairButton.IsEnabled) Pair_Click(sender, e);
    }

    private async void Pair_Click(object sender, RoutedEventArgs eventArgs)
    {
        PairButton.IsEnabled = false;
        PairButton.Content = "PAIRING…";
        ErrorPanel.Visibility = Visibility.Collapsed;
        ShowStatus("Contacting SRT and securing this rig…");
        try
        {
            var credential = await new RigPairingService().PairAsync(CodeInput.Text, SteamAccount.SignedInSteamId());
            try
            {
                new RigCredentialStore().Save(credential);
            }
            catch (Exception exception)
            {
                throw new PairingException("This rig was accepted by SRT, but Windows would not let the app save its rig key.",
                    "Run the app as the normal Windows user (not as administrator), get a new code from SRT admin and try again.",
                    contactSupport: true, exception);
            }

            ShowStatus($"✓ Paired as {credential.RigId}. Closing…");
            PairButton.Content = "PAIRED ✓";
            await Task.Delay(900);
            DialogResult = true;
        }
        catch (PairingException exception)
        {
            ShowError(exception.Message, exception.NextStep, exception.ContactSupport);
        }
        catch (Exception exception)
        {
            ShowError($"Pairing stopped unexpectedly: {exception.Message}", "Close this window and try again.", contactSupport: true);
        }
    }

    private void ShowError(string problem, string nextStep, bool contactSupport)
    {
        StatusOutput.Visibility = Visibility.Collapsed;
        ErrorProblem.Text = problem;
        ErrorNextStep.Text = nextStep;
        ErrorNextStep.Visibility = string.IsNullOrWhiteSpace(nextStep) ? Visibility.Collapsed : Visibility.Visible;
        // Every failure offers a way out; it is the main instruction when the sim centre cannot fix it themselves.
        ErrorSupport.Text = contactSupport
            ? "This can't be fixed from the rig. Contact the SRT Labs team and tell them the message above."
            : RigPairingService.SupportMessage;
        ErrorSupport.FontWeight = contactSupport ? FontWeights.SemiBold : FontWeights.Normal;
        ErrorPanel.Visibility = Visibility.Visible;
        PairButton.Content = "TRY AGAIN";
        PairButton.IsEnabled = true;
        CodeInput.Focus();
        CodeInput.SelectAll();
    }

    private void ShowStatus(string message)
    {
        StatusOutput.Text = message;
        StatusOutput.Visibility = Visibility.Visible;
    }
}
