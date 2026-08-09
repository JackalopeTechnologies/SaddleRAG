// RegistrationDialog.xaml.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows;

#endregion

namespace SaddleRAG.Tray;

/// <summary>
///     Two-field prompt used to record where a user-installed tool lives. Detection
///     supplies the defaults; the user confirms them. Nothing is ever registered
///     without the user pressing Save.
/// </summary>
public partial class RegistrationDialog : Window
{
    public RegistrationDialog(string intro,
                              string firstLabel,
                              string firstValue,
                              string secondLabel,
                              string secondValue)
    {
        InitializeComponent();
        IntroText.Text = intro;
        FirstLabel.Text = firstLabel;
        FirstValue.Text = firstValue;
        SecondLabel.Text = secondLabel;
        SecondValue.Text = secondValue;
    }

    public string FirstEntry => FirstValue.Text.Trim();

    public string SecondEntry => SecondValue.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
