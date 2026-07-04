using System;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Yuzu_Frontend.Desktop.Helpers
{
    /// <summary>
    /// 为 <see cref="CompositionVisual"/> 提供隐式动画创建的辅助方法。
    /// 通过创建 <see cref="ImplicitAnimationCollection"/> 并绑定到目标可视化对象，
    /// 使诸如位置、透明度、尺寸等属性变化时呈现平滑过渡效果。
    /// </summary>
    public class CompositionAnimationHelper
    {
        /// <summary>
        /// 为指定的 <see cref="CompositionVisual"/> 的位置（Offset）属性绑定隐式动画，
        /// 使位置变化时呈现平滑过渡。
        /// </summary>
        /// <param name="compositionVisual">要应用动画的可视化对象。</param>
        /// <param name="millis">动画时长，单位为毫秒，默认值为 250 毫秒。</param>
        public static void MakeScrollable(CompositionVisual compositionVisual, double millis = 250)
        {
            if (compositionVisual == null)
                return;

            // 获取当前可视化对象所属的合成器（Compositor），由其负责创建动画资源
            Compositor compositor = compositionVisual.Compositor;

            var animationGroup = compositor.CreateAnimationGroup();
            // 创建一个针对 Offset 属性的三维关键帧动画
            Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";

            // 使用表达式关键帧，将最终值设置为目标属性的最终值（this.FinalValue）
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            // 设置动画持续时间
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 构造隐式动画集合并绑定到可视化对象
            ImplicitAnimationCollection implicitAnimationCollection = compositor.CreateImplicitAnimationCollection();
            animationGroup.Add(offsetAnimation);
            implicitAnimationCollection["Offset"] = animationGroup;
            compositionVisual.ImplicitAnimations = implicitAnimationCollection;
        }

        /// <summary>
        /// 为指定的 <see cref="CompositionVisual"/> 的透明度（Opacity）与位置（Offset）属性
        /// 绑定隐式动画，使显示/隐藏以及位置变化时带有淡入淡出与位移过渡。
        /// </summary>
        /// <param name="compositionVisual">要应用动画的可视化对象。</param>
        /// <param name="millis">动画时长，单位为毫秒，默认值为 700 毫秒。</param>
        public static void MakeOpacityAnimated(CompositionVisual compositionVisual, double millis = 700)
        {
            if (compositionVisual == null)
                return;

            Compositor compositor = compositionVisual.Compositor;

            var animationGroup = compositor.CreateAnimationGroup();

            // 创建并绑定 Opacity 关键帧动画
            ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Target = "Opacity";
            opacityAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            opacityAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 创建并绑定 Offset 关键帧动画
            Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 组合动画组并添加到隐式动画集合
            animationGroup.Add(offsetAnimation);
            animationGroup.Add(opacityAnimation);

            ImplicitAnimationCollection implicitAnimationCollection = compositor.CreateImplicitAnimationCollection();
            implicitAnimationCollection["Opacity"] = animationGroup;
            implicitAnimationCollection["Offset"] = animationGroup;

            compositionVisual.ImplicitAnimations = implicitAnimationCollection;
        }

        /// <summary>
        /// 为指定的 <see cref="CompositionVisual"/> 的尺寸（Size）与位置（Offset）属性
        /// 绑定隐式动画，使尺寸变化时呈现平滑过渡并配合位置调整。
        /// </summary>
        /// <param name="compositionVisual">要应用动画的可视化对象。</param>
        /// <param name="millis">动画时长，单位为毫秒，默认值为 450 毫秒。</param>
        public static void MakeSizeAnimated(CompositionVisual compositionVisual, double millis = 450)
        {
            if (compositionVisual == null)
                return;

            Compositor compositor = compositionVisual.Compositor;

            var animationGroup = compositor.CreateAnimationGroup();

            // 绑定 Size 属性的二维关键帧动画
            Vector2KeyFrameAnimation sizeAnimation = compositor.CreateVector2KeyFrameAnimation();
            sizeAnimation.Target = "Size";
            sizeAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            sizeAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 绑定 Offset 属性的三维关键帧动画
            Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 组合并设置隐式动画
            animationGroup.Add(sizeAnimation);
            animationGroup.Add(offsetAnimation);

            ImplicitAnimationCollection implicitAnimationCollection = compositor.CreateImplicitAnimationCollection();
            implicitAnimationCollection["Size"] = animationGroup;
            implicitAnimationCollection["Offset"] = animationGroup;

            compositionVisual.ImplicitAnimations = implicitAnimationCollection;
        }

        /// <summary>
        /// 为指定的 <see cref="CompositionVisual"/> 的尺寸（Size）、透明度（Opacity）
        /// 与位置（Offset）属性同时绑定隐式动画，使尺寸/透明度/位置变化时均有平滑过渡。
        /// </summary>
        /// <param name="compositionVisual">要应用动画的可视化对象。</param>
        /// <param name="millis">动画时长，单位为毫秒，默认值为 450 毫秒。</param>
        public static void MakeSizeOpacityAnimated(CompositionVisual compositionVisual, double millis = 450)
        {
            if (compositionVisual == null)
                return;

            Compositor compositor = compositionVisual.Compositor;

            var animationGroup = compositor.CreateAnimationGroup();

            // Size 二维关键帧动画
            Vector2KeyFrameAnimation sizeAnimation = compositor.CreateVector2KeyFrameAnimation();
            sizeAnimation.Target = "Size";
            sizeAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            sizeAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // Offset 三维关键帧动画
            Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // Opacity 标量关键帧动画
            ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Target = "Opacity";
            opacityAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            opacityAnimation.Duration = TimeSpan.FromMilliseconds(millis);

            // 将三者组合为一个动画组，并在三个目标属性上触发
            animationGroup.Add(sizeAnimation);
            animationGroup.Add(opacityAnimation);
            animationGroup.Add(offsetAnimation);

            ImplicitAnimationCollection implicitAnimationCollection = compositor.CreateImplicitAnimationCollection();
            implicitAnimationCollection["Size"] = animationGroup;
            implicitAnimationCollection["Opacity"] = animationGroup;
            implicitAnimationCollection["Offset"] = animationGroup;

            compositionVisual.ImplicitAnimations = implicitAnimationCollection;
        }
    }
}
