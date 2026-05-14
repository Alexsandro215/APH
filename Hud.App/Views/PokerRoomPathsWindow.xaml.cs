using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Hud.App.Services;
using Microsoft.Win32;

namespace Hud.App.Views
{
    public partial class PokerRoomPathsWindow : Window
    {
        private readonly AppSettings _settings;

        public static IReadOnlyList<string> DefaultRooms { get; } =
            ["PokerStars", "GGPoker", "888poker", "WPN", "PartyPoker", "iPoker"];

        public ObservableCollection<PokerRoomPathRow> Rooms { get; } = new();
        public string? SelectedFolder { get; private set; }
        public string? SelectedRoom { get; private set; }

        public PokerRoomPathsWindow()
        {
            InitializeComponent();
            _settings = AppSettingsService.Load();
            DataContext = this;

            foreach (var room in DefaultRooms)
                AddRoom(room, GetRoomDescription(room));
        }

        private static string GetRoomDescription(string room) =>
            room switch
            {
                "PokerStars" => "Historias de manos de PokerStars.",
                "GGPoker" => LocalizationManager.Text("Rooms.LocalExports"),
                "888poker" => LocalizationManager.Text("Rooms.LocalHistories"),
                "WPN" => "Winning Poker Network / Americas Cardroom.",
                "PartyPoker" => LocalizationManager.Text("Rooms.LocalHistories"),
                "iPoker" => "Red iPoker y skins compatibles.",
                _ => LocalizationManager.Text("Rooms.LocalHistories")
            };

        private void AddRoom(string name, string description)
        {
            var folder = "";
            if (_settings.PokerRoomFolders.TryGetValue(name, out var savedFolder))
                folder = savedFolder;

            Rooms.Add(new PokerRoomPathRow(name, description, folder));
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PokerRoomPathRow row)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = string.Format(LocalizationManager.Text("Rooms.SelectFolderFor"), row.Name)
            };

            if (!string.IsNullOrWhiteSpace(row.Folder) && Directory.Exists(row.Folder))
                dlg.InitialDirectory = row.Folder;

            if (dlg.ShowDialog(this) == true)
            {
                row.Folder = dlg.FolderName;
                SaveRows(row.Name);
                StatusText.Text = string.Format(LocalizationManager.Text("Rooms.SavedFor"), row.Name);
            }
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PokerRoomPathRow row)
                return;

            if (string.IsNullOrWhiteSpace(row.Folder) || !Directory.Exists(row.Folder))
            {
                StatusText.Text = string.Format(LocalizationManager.Text("Rooms.SelectValidFolder"), row.Name);
                return;
            }

            SaveRows(row.Name);
            SelectedRoom = row.Name;
            SelectedFolder = row.Folder;
            DialogResult = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SaveRows(_settings.SelectedPokerRoom);
            DialogResult = false;
        }

        private void SaveRows(string selectedRoom)
        {
            foreach (var room in Rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Folder))
                    _settings.PokerRoomFolders.Remove(room.Name);
                else
                    _settings.PokerRoomFolders[room.Name] = room.Folder.Trim();
            }

            _settings.SelectedPokerRoom = selectedRoom;
            _settings.PokerStarsHandHistoryFolder = _settings.PokerRoomFolders.TryGetValue("PokerStars", out var pokerStarsFolder)
                ? pokerStarsFolder
                : null;

            AppSettingsService.Save(_settings);
        }
    }

    public sealed class PokerRoomPathRow : INotifyPropertyChanged
    {
        private string _folder;

        public PokerRoomPathRow(string name, string description, string folder)
        {
            Name = name;
            Description = description;
            _folder = folder;
        }

        public string Name { get; }
        public string Description { get; }

        public string Folder
        {
            get => _folder;
            set
            {
                if (_folder == value) return;
                _folder = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
