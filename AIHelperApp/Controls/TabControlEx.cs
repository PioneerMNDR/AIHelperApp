using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AIHelperApp.Controls
{
    [TemplatePart(Name = "PART_ItemsHolder", Type = typeof(Panel))]
    public class TabControlEx : TabControl
    {
        private Panel _itemsHolderPanel;

        public TabControlEx()
        {
            ItemContainerGenerator.StatusChanged += ItemContainerGenerator_StatusChanged;
            // ← Подписка на Loaded для гарантии загрузки всех вкладок
            Loaded += TabControlEx_Loaded;
        }

        private void TabControlEx_Loaded(object sender, RoutedEventArgs e)
        {
            // Гарантируем создание всех вкладок после полной загрузки
            EnsureAllTabsLoaded();
        }

        private void ItemContainerGenerator_StatusChanged(object sender, EventArgs e)
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            {
                ItemContainerGenerator.StatusChanged -= ItemContainerGenerator_StatusChanged;
                // ← Загружаем ВСЕ вкладки, а не только выбранную
                EnsureAllTabsLoaded();
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _itemsHolderPanel = GetTemplateChild("PART_ItemsHolder") as Panel;
            EnsureAllTabsLoaded();
        }

        protected override void OnItemsChanged(
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);
            if (_itemsHolderPanel == null) return;

            switch (e.Action)
            {
                case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                    _itemsHolderPanel.Children.Clear();
                    break;

                case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            var cp = FindChildContentPresenter(item);
                            if (cp != null)
                                _itemsHolderPanel.Children.Remove(cp);
                        }
                    }
                    // ← Загружаем ВСЕ (включая новые)
                    EnsureAllTabsLoaded();
                    break;

                case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                    throw new NotImplementedException("Replace not implemented yet");
            }
        }

        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            base.OnSelectionChanged(e);
            UpdateVisibility();
        }

        // ══════════════════════════════════════════════════════════
        //  КЛЮЧЕВОЙ МЕТОД: создаёт ContentPresenter для КАЖДОЙ вкладки
        // ══════════════════════════════════════════════════════════
        private void EnsureAllTabsLoaded()
        {
            if (_itemsHolderPanel == null) return;

            // Проходим по ВСЕМ элементам, а не только по выбранному
            for (int i = 0; i < Items.Count; i++)
            {
                TabItem tabItem = Items[i] as TabItem
                    ?? ItemContainerGenerator.ContainerFromIndex(i) as TabItem;

                if (tabItem != null)
                {
                    CreateChildContentPresenter(tabItem);
                }
            }

            UpdateVisibility();
        }

        // ══════════════════════════════════════════════════════════
        //  Только переключает Visibility, НЕ создаёт ничего
        // ══════════════════════════════════════════════════════════
        private void UpdateVisibility()
        {
            if (_itemsHolderPanel == null) return;

            foreach (ContentPresenter child in _itemsHolderPanel.Children)
            {
                child.Visibility = ((child.Tag as TabItem)?.IsSelected == true)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private ContentPresenter CreateChildContentPresenter(object item)
        {
            if (item == null) return null;

            var cp = FindChildContentPresenter(item);
            if (cp != null) return cp;

            var tabItem = item as TabItem
                ?? ItemContainerGenerator.ContainerFromItem(item) as TabItem;
            if (tabItem == null) return null;

            cp = new ContentPresenter
            {
                Content = tabItem.Content,
                ContentTemplate = ContentTemplate,
                ContentTemplateSelector = ContentTemplateSelector,
                ContentStringFormat = ContentStringFormat,
                // ← Начальная видимость по статусу выбора
                Visibility = tabItem.IsSelected
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                Tag = tabItem
            };

            _itemsHolderPanel.Children.Add(cp);
            return cp;
        }

        private ContentPresenter FindChildContentPresenter(object item)
        {
            if (item == null || _itemsHolderPanel == null) return null;

            var tabItem = item as TabItem
                ?? ItemContainerGenerator.ContainerFromItem(item) as TabItem;

            foreach (ContentPresenter cp in _itemsHolderPanel.Children)
            {
                if (cp.Tag == tabItem)
                    return cp;
            }
            return null;
        }
    }
}