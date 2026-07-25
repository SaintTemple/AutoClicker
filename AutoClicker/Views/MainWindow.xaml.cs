using AutoClicker.Services;
using AutoClicker.ViewModels;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoClicker.Views;

public partial class MainWindow : Window
{
    private static readonly Regex NumericRegex = new(@"^\d*([.]\d*)?$", RegexOptions.Compiled);

    private readonly MainViewModel _viewModel;
    private bool _isShutdownInProgress;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        IInputSimulationService inputService = new SendInputSimulationService();
        IAutoClickService autoClickService = new AutoClickService(inputService);
        IHotkeyService hotkeyService = new GlobalHotkeyService();

        _viewModel = new MainViewModel(autoClickService, hotkeyService);
        DataContext = _viewModel;

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        DataObject.AddPastingHandler(IntervalValueTextBox, IntervalValueTextBox_OnPaste);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _viewModel.Initialize(this);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;

        if (_isShutdownInProgress)
        {
            return;
        }

        _isShutdownInProgress = true;
        try
        {
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _allowClose = true;
            _isShutdownInProgress = false;
            Close();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_viewModel.HandleKeyCapture(key))
        {
            e.Handled = true;
        }
    }

    private void IntervalValueTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var candidate = BuildCandidateText(textBox, e.Text);
        e.Handled = !NumericRegex.IsMatch(candidate);
    }

    private void IntervalValueTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        if (sender is not TextBox textBox)
        {
            e.CancelCommand();
            return;
        }

        var candidate = BuildCandidateText(textBox, pastedText);
        if (!NumericRegex.IsMatch(candidate))
        {
            e.CancelCommand();
        }
    }

    private static string BuildCandidateText(TextBox textBox, string input)
    {
        var current = textBox.Text ?? string.Empty;
        var selectionStart = textBox.SelectionStart;
        var selectionLength = textBox.SelectionLength;

        if (selectionLength > 0)
        {
            current = current.Remove(selectionStart, selectionLength);
        }

        return current.Insert(selectionStart, input);
    }
}