using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace VideoCompare
{
    /// <summary>
    /// 界面美化：进度条 / 音量条 / 滚动条统一成扁平深色样式。
    /// 用运行时解析 XAML 资源字典实现（csc 直接编译，无需 XAML 编译步骤）。
    /// </summary>
    public static class Styles
    {
        const string Xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

  <!-- ============ 播放进度条 / 音量条：扁平圆角 ============ -->

  <Style x:Key=""FlatThumb"" TargetType=""Thumb"">
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Thumb"">
          <Grid Background=""Transparent"">
            <Ellipse x:Name=""dot"" Width=""13"" Height=""13""
                     Fill=""#eef2f8"" Stroke=""#4d8dff"" StrokeThickness=""2""/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""dot"" Property=""Width"" Value=""16""/>
              <Setter TargetName=""dot"" Property=""Height"" Value=""16""/>
              <Setter TargetName=""dot"" Property=""Fill"" Value=""#ffffff""/>
            </Trigger>
            <Trigger Property=""IsDragging"" Value=""True"">
              <Setter TargetName=""dot"" Property=""Width"" Value=""16""/>
              <Setter TargetName=""dot"" Property=""Height"" Value=""16""/>
              <Setter TargetName=""dot"" Property=""Fill"" Value=""#bcd4ff""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key=""FlatTrackFill"" TargetType=""RepeatButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""RepeatButton"">
          <Border Height=""5"" CornerRadius=""3"" Background=""#4d8dff"" VerticalAlignment=""Center""/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key=""FlatTrackRest"" TargetType=""RepeatButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""RepeatButton"">
          <Border Height=""5"" CornerRadius=""3"" Background=""#2c3444"" VerticalAlignment=""Center""/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 底槽透明（真正的深色槽由模板里静态 Border 画，好让缓存条夹在两者中间） -->
  <Style x:Key=""FlatTrackClear"" TargetType=""RepeatButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""RepeatButton"">
          <Border Height=""5"" Background=""Transparent"" VerticalAlignment=""Center""/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 播放进度条 -->
  <Style x:Key=""SeekSlider"" TargetType=""Slider"">
    <Setter Property=""Height"" Value=""20""/>
    <Setter Property=""VerticalAlignment"" Value=""Center""/>
    <Setter Property=""IsMoveToPointEnabled"" Value=""True""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Slider"">
          <Grid Background=""Transparent"" VerticalAlignment=""Stretch"">
            <!-- 未缓存的底槽 -->
            <Border Height=""5"" CornerRadius=""3"" Background=""#2c3444"" VerticalAlignment=""Center""/>
            <Track x:Name=""PART_Track"">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style=""{StaticResource FlatTrackFill}""/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style=""{StaticResource FlatThumb}""/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style=""{StaticResource FlatTrackClear}""/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.4""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 音量条：细一些、拇指小一些 -->
  <Style x:Key=""VolumeThumb"" TargetType=""Thumb"">
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Thumb"">
          <Grid Background=""Transparent"">
            <Ellipse x:Name=""dot"" Width=""10"" Height=""10"" Fill=""#9aa6bb""/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""dot"" Property=""Width"" Value=""12""/>
              <Setter TargetName=""dot"" Property=""Height"" Value=""12""/>
              <Setter TargetName=""dot"" Property=""Fill"" Value=""#4d8dff""/>
            </Trigger>
            <Trigger Property=""IsDragging"" Value=""True"">
              <Setter TargetName=""dot"" Property=""Fill"" Value=""#4d8dff""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key=""VolumeSlider"" TargetType=""Slider"">
    <Setter Property=""Height"" Value=""18""/>
    <Setter Property=""VerticalAlignment"" Value=""Center""/>
    <Setter Property=""IsMoveToPointEnabled"" Value=""True""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Slider"">
          <Grid Background=""Transparent"" VerticalAlignment=""Center"">
            <Border Height=""3"" CornerRadius=""2"" Background=""#2c3444"" VerticalAlignment=""Center""/>
            <Track x:Name=""PART_Track"">
              <Track.DecreaseRepeatButton>
                <RepeatButton Focusable=""False"">
                  <RepeatButton.Template>
                    <ControlTemplate TargetType=""RepeatButton"">
                      <Border Height=""3"" CornerRadius=""2"" Background=""#5f7fa8"" VerticalAlignment=""Center""/>
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style=""{StaticResource VolumeThumb}""/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Focusable=""False"">
                  <RepeatButton.Template>
                    <ControlTemplate TargetType=""RepeatButton"">
                      <Border Background=""Transparent"" Height=""3""/>
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.4""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 静音开关：透明底 + 悬停淡高亮（图标本身在代码里画，方便切换斜杠） -->
  <Style x:Key=""MuteToggle"" TargetType=""ToggleButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""Width"" Value=""26""/>
    <Setter Property=""Height"" Value=""24""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ToggleButton"">
          <Border x:Name=""bg"" Background=""Transparent"" CornerRadius=""5"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bg"" Property=""Background"" Value=""#232936""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.4""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ============ 滚动条：细、圆角、悬停变亮 ============ -->

  <Style x:Key=""FlatScrollThumb"" TargetType=""Thumb"">
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Thumb"">
          <Border x:Name=""bar"" CornerRadius=""4"" Background=""#39415299"" Margin=""2""/>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bar"" Property=""Background"" Value=""#5a657dcc""/>
            </Trigger>
            <Trigger Property=""IsDragging"" Value=""True"">
              <Setter TargetName=""bar"" Property=""Background"" Value=""#6f7d99ee""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType=""ScrollBar"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Width"" Value=""10""/>
    <Setter Property=""MinWidth"" Value=""10""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ScrollBar"">
          <Grid Background=""Transparent"" Margin=""1"">
            <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
              <Track.DecreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" Focusable=""False""/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style=""{StaticResource FlatScrollThumb}"" MinHeight=""36""/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" Focusable=""False""/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""Orientation"" Value=""Horizontal"">
              <Setter Property=""Width"" Value=""Auto""/>
              <Setter TargetName=""PART_Track"" Property=""IsDirectionReversed"" Value=""False""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property=""Orientation"" Value=""Horizontal"">
        <Setter Property=""Width"" Value=""Auto""/>
        <Setter Property=""Height"" Value=""10""/>
        <Setter Property=""MinHeight"" Value=""10""/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- ============ 下拉框（倍速）============ -->
  <Style TargetType=""ComboBoxItem"">
    <Setter Property=""Foreground"" Value=""#c8d0de""/>
    <Setter Property=""Padding"" Value=""9,5""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ComboBoxItem"">
          <Border x:Name=""bg"" Background=""Transparent"" CornerRadius=""6"" Margin=""3,1"">
            <ContentPresenter Margin=""6,3""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsHighlighted"" Value=""True"">
              <Setter TargetName=""bg"" Property=""Background"" Value=""#2b3546""/>
            </Trigger>
            <Trigger Property=""IsSelected"" Value=""True"">
              <Setter TargetName=""bg"" Property=""Background"" Value=""#2f4a7a""/>
              <Setter Property=""Foreground"" Value=""#ffffff""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key=""FlatCombo"" TargetType=""ComboBox"">  

    <Setter Property=""Height"" Value=""26""/>
    <Setter Property=""Padding"" Value=""8,0,4,0""/>
    <Setter Property=""Foreground"" Value=""#c8d0de""/>
    <Setter Property=""Background"" Value=""#1c212b""/>
    <Setter Property=""BorderBrush"" Value=""#2a3140""/>
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ComboBox"">
          <Grid>
            <ToggleButton x:Name=""ToggleButton"" Focusable=""False"" ClickMode=""Press""
                          IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"">
              <ToggleButton.Template>
                <ControlTemplate TargetType=""ToggleButton"">
                  <Border CornerRadius=""7"" Background=""#1c212b"" BorderBrush=""#2a3140"" BorderThickness=""1"">
                    <Path x:Name=""arrow"" Data=""M 0 0 L 4 4 L 8 0"" Stroke=""#8d97ab"" StrokeThickness=""1.4""
                          HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,7,0""/>
                  </Border>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin=""8,0,20,0"" VerticalAlignment=""Center"" HorizontalAlignment=""Left""
                              Content=""{TemplateBinding SelectionBoxItem}""
                              ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""/>
            <Popup x:Name=""PART_Popup"" Placement=""Bottom"" AllowsTransparency=""True""
                   IsOpen=""{TemplateBinding IsDropDownOpen}"">
              <Border Background=""#1c212b"" BorderBrush=""#2a3140"" BorderThickness=""1"" CornerRadius=""8"" Margin=""0,4,0,0"">
                <ScrollViewer MaxHeight=""260"">
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>";

        static ResourceDictionary dict;

        static ResourceDictionary Dict
        {
            get
            {
                if (dict == null)
                {
                    dict = (ResourceDictionary)XamlReader.Parse(Xaml);
                }
                return dict;
            }
        }

        /// <summary>把资源字典挂到窗口上（隐式 ScrollBar 样式会对所有滚动条生效）。</summary>
        public static void Apply(Window window)
        {
            foreach (System.Collections.DictionaryEntry entry in Dict)
            {
                window.Resources[entry.Key] = entry.Value;
            }
        }

        public static Style Get(string key)
        {
            return Dict[key] as Style;
        }
    }
}
