using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Hud.App.Services;

namespace Hud.App
{
    /// <summary>
    /// Navegacion de una sola ventana. Las funciones de analisis siguen escritas como ventanas
    /// (GlobalAnalysisWindow, MisTablasWindow...), pero aqui se toma su contenido y se muestra
    /// dentro del area de paginas, sin abrir otra ventana. La ventana original nunca se muestra:
    /// solo conserva su codigo, sus recursos y su DataContext, que el contenido sigue usando.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class PageEntry
        {
            public required string Key { get; init; }
            public required string Title { get; init; }
            public required FrameworkElement Content { get; init; }
            public Window? Source { get; init; }
            public RadioButton? NavItem { get; init; }
            public bool IsDetail { get; init; }

            /// <summary>Linea de resumen de la pagina (por ejemplo "Mesa: 14/14") que se muestra bajo el titulo.</summary>
            public TextBlock? Subtitle { get; init; }

            /// <summary>Como volver a crear la pagina (solo las del menu).</summary>
            public Func<Window>? Factory { get; init; }
        }

        private readonly Dictionary<string, PageEntry> _pageCache = new(StringComparer.Ordinal);
        private readonly Stack<PageEntry> _backStack = new();
        private PageEntry? _dashboardEntry;
        private PageEntry? _currentPage;

        private PageEntry DashboardEntry => _dashboardEntry ??= new PageEntry
        {
            Key = "dashboard",
            Title = LocalizationManager.Text("Main.NavDashboard"),
            Content = DashboardPage,
            NavItem = NavDashboard
        };

        private void ShowDashboardPage()
        {
            DisposeBackStack();
            ShowEntry(DashboardEntry);
        }

        /// <summary>Pagina principal de una seccion del menu. Se guarda para volver a ella sin recalcular.</summary>
        private void NavigateToPage(string key, RadioButton navItem, Func<Window> factory)
        {
            DisposeBackStack();

            if (!_pageCache.TryGetValue(key, out var entry))
            {
                entry = Embed(key, factory(), navItem, isDetail: false, factory);
                _pageCache[key] = entry;
            }

            ShowEntry(entry);
        }

        /// <summary>
        /// Tras cambiar de aspecto. WPF congela los pinceles del tema, asi que el cambio pone pinceles
        /// nuevos: lo que una pagina ya habia pintado (celdas de estadisticas, graficas dibujadas por
        /// codigo) se quedaba con los colores del tema anterior hasta reiniciar APH. Las paginas
        /// guardadas se descartan y la que se estaba viendo se vuelve a crear con los colores nuevos.
        /// </summary>
        private void RebuildPagesAfterPaletteChange()
        {
            var showing = _currentPage is { IsDetail: true }
                ? _backStack.FirstOrDefault(e => !e.IsDetail)
                : _currentPage;
            var reopen = showing is { Factory: not null, NavItem: not null } ? showing : null;

            DisposeBackStack();
            foreach (var entry in _pageCache.Values)
                Release(entry);
            _pageCache.Clear();

            if (reopen is not null)
                NavigateToPage(reopen.Key, reopen.NavItem!, reopen.Factory!);
            else if (_currentPage != DashboardEntry)
                ShowEntry(DashboardEntry);
        }

        /// <summary>
        /// Detalle abierto desde una pagina (una mesa, un villano...). Se apila encima de la pagina
        /// actual y el boton "Volver" regresa a ella.
        /// </summary>
        public void OpenDetailPage(Window window)
        {
            if (_currentPage is not null)
                _backStack.Push(_currentPage);

            ShowEntry(Embed($"detail:{Guid.NewGuid():N}", window, _currentPage?.NavItem, isDetail: true));
        }

        private void GoBack()
        {
            if (_backStack.Count == 0)
                return;

            var leaving = _currentPage;
            var previous = _backStack.Pop();
            ShowEntry(previous);

            if (leaving is { IsDetail: true })
                Release(leaving);
        }

        /// <summary>Al recargar las manos, las paginas guardadas tienen datos viejos: se descartan.</summary>
        private void ResetPageCache()
        {
            var showingCached = _currentPage is not null && _currentPage != DashboardEntry;
            DisposeBackStack();

            foreach (var entry in _pageCache.Values)
                Release(entry);
            _pageCache.Clear();

            if (showingCached)
                ShowEntry(DashboardEntry);
        }

        private void DisposeBackStack()
        {
            if (_currentPage is { IsDetail: true } detail)
            {
                _currentPage = null;
                Release(detail);
            }

            while (_backStack.Count > 0)
            {
                var entry = _backStack.Pop();
                if (entry.IsDetail)
                    Release(entry);
            }
        }

        private void ShowEntry(PageEntry entry)
        {
            PageHost.Content = entry.Content;
            _currentPage = entry;
            PageTitleText.Text = entry.Title;

            BindingOperations.ClearBinding(PageSubtitleText, TextBlock.TextProperty);
            if (entry.Subtitle is not null)
                PageSubtitleText.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = entry.Subtitle });
            else if (entry == DashboardEntry)
                PageSubtitleText.SetResourceReference(TextBlock.TextProperty, "Main.Subtitle");
            else
                PageSubtitleText.Text = "";
            BackButton.Visibility = _backStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncNavSelection();
        }

        /// <summary>Deja marcado en el menu el elemento de la pagina que se ve.</summary>
        private void SyncNavSelection()
        {
            var item = _currentPage?.NavItem ?? NavDashboard;
            item.IsChecked = true;
        }

        private static PageEntry Embed(string key, Window window, RadioButton? navItem, bool isDetail, Func<Window>? factory = null)
        {
            var content = window.Content as FrameworkElement ?? new ContentControl { Content = window.Content };
            window.Content = null;

            // Recursos propios de la ventana (estilos implicitos, convertidores, plantillas).
            content.Resources.MergedDictionaries.Add(window.Resources);

            // El contenido sigue al DataContext de su ventana, aunque se asigne despues (las
            // paginas que cargan en segundo plano lo ponen al terminar).
            if (content.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
            {
                BindingOperations.SetBinding(
                    content,
                    FrameworkElement.DataContextProperty,
                    new Binding(nameof(DataContext)) { Source = window });
            }

            // Las ventanas hacen trabajo en su evento Loaded (dibujar graficas, seleccionar la mano
            // inicial). Como nunca se muestran, se les lanza cuando su contenido aparece.
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                content.Loaded -= onLoaded;
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Pagina {window.GetType().Name}: fallo al iniciar", ex);
                }
            };
            content.Loaded += onLoaded;

            var subtitle = PromotePageHeader(content);

            return new PageEntry
            {
                Subtitle = subtitle,
                Key = key,
                Title = CleanTitle(window.Title),
                Content = content,
                Source = window,
                NavItem = navItem,
                IsDetail = isDetail,
                Factory = factory
            };
        }

        /// <summary>
        /// Muchas paginas empiezan con una tarjeta que solo lleva su titulo en grande y una linea de
        /// resumen. Con la barra superior ya mostrando el titulo, esa tarjeta sobra: se oculta y el
        /// resumen pasa debajo del titulo comun. Si la tarjeta tiene botones, campos o mas datos, se
        /// deja como esta.
        /// </summary>
        private static TextBlock? PromotePageHeader(FrameworkElement content)
        {
            try
            {
                var root = content;
                // BackgroundLoad envuelve el contenido en un Grid con la capa de "cargando".
                if (root is Grid host && host.Children.Count > 0 && host.Children[0] is FrameworkElement inner &&
                    host.RowDefinitions.Count == 0 && host.ColumnDefinitions.Count == 0)
                    root = inner;

                // Algunas paginas envuelven todo en un ScrollViewer o un Border.
                for (var depth = 0; depth < 2; depth++)
                {
                    if (root is ScrollViewer { Content: FrameworkElement scrolled })
                        root = scrolled;
                    else if (root is Decorator { Child: FrameworkElement decorated } && root is not Border)
                        root = decorated;
                }

                if (root is not Panel panel)
                    return null;

                var header = panel.Children.OfType<Border>().FirstOrDefault();
                if (header is null || header.Visibility != Visibility.Visible)
                    return null;

                var nodes = LogicalDescendants(header).ToList();
                if (nodes.Any(n => n is System.Windows.Controls.Primitives.ButtonBase or TextBox or System.Windows.Controls.Primitives.Selector or ItemsControl))
                    return null;

                var texts = nodes.OfType<TextBlock>().ToList();
                if (texts.Count is 0 or > 3)
                    return null;

                var title = texts.FirstOrDefault(tb => tb.FontSize >= 20);
                if (title is null)
                    return null;

                header.Visibility = Visibility.Collapsed;
                return texts.FirstOrDefault(tb => tb != title);
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo adaptar la cabecera de una pagina", ex);
                return null;
            }
        }

        /// <summary>
        /// Vuelve a evaluar los enlaces de datos de una parte de la ventana. Los convertidores de
        /// color (por ejemplo, el fondo de las estadisticas con pocas manos) toman el pincel del tema
        /// en ese momento; tras cambiar de aspecto hay que pedirselo otra vez.
        /// </summary>
        private static void RefreshBindings(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                var values = node.GetLocalValueEnumerator();
                while (values.MoveNext())
                {
                    if (values.Current.Value is BindingExpressionBase expression)
                    {
                        try { expression.UpdateTarget(); }
                        catch (Exception ex) { AppLog.Warn("No se pudo refrescar un enlace tras cambiar de aspecto", ex); }
                    }
                }

                if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (var i = 0; i < count; i++)
                        pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in LogicalDescendants(child))
                    yield return nested;
            }
        }

        private static void Release(PageEntry entry)
        {
            if (entry.Source is null)
                return;

            try
            {
                // Una ventana creada y nunca mostrada sigue en Application.Windows hasta cerrarla.
                entry.Source.Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"No se pudo liberar la pagina {entry.Key}", ex);
            }
        }

        private static string CleanTitle(string? title)
        {
            var text = (title ?? "").Trim();
            if (text.StartsWith("APH - ", StringComparison.OrdinalIgnoreCase))
                text = text[6..];
            return text.Length == 0 ? "APH" : text;
        }
    }
}
