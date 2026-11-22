// ============================================================================
// LiteTube 2.0 (Rust) — Ícones Vetoriais Monocromáticos (Estilo YouTube / XAML)
// Substitui emojis por geometria vetorial limpa idêntica aos Paths SVG do WPF.
// ============================================================================

#![allow(dead_code)]

use eframe::egui::{self, Color32, CornerRadius, Pos2, Rect, Stroke, Vec2};

pub fn draw_hamburger(painter: &egui::Painter, rect: Rect, color: Color32) {
    let w = rect.width() * 0.50;
    let cx = rect.center().x;
    let cy = rect.center().y;
    let stroke = Stroke::new(1.8_f32, color);
    for dy in [-5.0_f32, 0.0, 5.0] {
        painter.line_segment(
            [Pos2::new(cx - w * 0.5, cy + dy), Pos2::new(cx + w * 0.5, cy + dy)],
            stroke,
        );
    }
}

pub fn draw_play_triangle(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.5;
    let pts = vec![
        Pos2::new(center.x - r * 0.65, center.y - r * 0.85),
        Pos2::new(center.x + r * 0.95, center.y),
        Pos2::new(center.x - r * 0.65, center.y + r * 0.85),
    ];
    painter.add(egui::Shape::convex_polygon(pts, color, Stroke::NONE));
}

pub fn draw_pause_bars(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let bar_w = (size * 0.24).max(2.4);
    let bar_h = size * 0.82;
    let gap = size * 0.22;
    let left = Rect::from_center_size(
        Pos2::new(center.x - gap * 0.5 - bar_w * 0.5, center.y),
        Vec2::new(bar_w, bar_h),
    );
    let right = Rect::from_center_size(
        Pos2::new(center.x + gap * 0.5 + bar_w * 0.5, center.y),
        Vec2::new(bar_w, bar_h),
    );
    painter.rect_filled(left, CornerRadius::same(1), color);
    painter.rect_filled(right, CornerRadius::same(1), color);
}

pub fn draw_next_track(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.48;
    let pts = vec![
        Pos2::new(center.x - r * 0.95, center.y - r * 0.78),
        Pos2::new(center.x + r * 0.35, center.y),
        Pos2::new(center.x - r * 0.95, center.y + r * 0.78),
    ];
    painter.add(egui::Shape::convex_polygon(pts, color, Stroke::NONE));
    let bar = Rect::from_min_max(
        Pos2::new(center.x + r * 0.48, center.y - r * 0.76),
        Pos2::new(center.x + r * 0.88, center.y + r * 0.76),
    );
    painter.rect_filled(bar, CornerRadius::same(1), color);
}

pub fn draw_prev_track(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.48;
    let bar = Rect::from_min_max(
        Pos2::new(center.x - r * 0.88, center.y - r * 0.76),
        Pos2::new(center.x - r * 0.48, center.y + r * 0.76),
    );
    painter.rect_filled(bar, CornerRadius::same(1), color);
    let pts = vec![
        Pos2::new(center.x + r * 0.95, center.y - r * 0.78),
        Pos2::new(center.x - r * 0.35, center.y),
        Pos2::new(center.x + r * 0.95, center.y + r * 0.78),
    ];
    painter.add(egui::Shape::convex_polygon(pts, color, Stroke::NONE));
}

pub fn draw_search(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.33;
    let c = Pos2::new(center.x - size * 0.12, center.y - size * 0.12);
    painter.circle_stroke(c, r, Stroke::new(1.7_f32, color));
    let d = r * 0.72;
    painter.line_segment(
        [
            Pos2::new(c.x + d, c.y + d),
            Pos2::new(center.x + size * 0.44, center.y + size * 0.44),
        ],
        Stroke::new(1.9_f32, color),
    );
}

pub fn draw_refresh(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.38;
    let mut pts = Vec::with_capacity(18);
    for i in 0..=16 {
        let a = -0.35_f32 + (i as f32 / 16.0) * 4.85_f32;
        pts.push(Pos2::new(center.x + r * a.cos(), center.y + r * a.sin()));
    }
    painter.add(egui::Shape::line(pts, Stroke::new(1.7_f32, color)));
    // Ponta da seta no topo direito
    let tip = Pos2::new(center.x + r * 0.92, center.y - r * 0.35);
    let arrow = vec![
        tip,
        Pos2::new(tip.x - 4.5, tip.y - 2.0),
        Pos2::new(tip.x + 0.5, tip.y - 5.5),
    ];
    painter.add(egui::Shape::convex_polygon(arrow, color, Stroke::NONE));
}

pub fn draw_home(painter: &egui::Painter, center: Pos2, size: f32, color: Color32, filled: bool) {
    let r = size * 0.46;
    let roof_top = Pos2::new(center.x, center.y - r);
    let roof_l = Pos2::new(center.x - r, center.y - r * 0.05);
    let roof_r = Pos2::new(center.x + r, center.y - r * 0.05);
    let wall_l_top = Pos2::new(center.x - r * 0.68, center.y - r * 0.12);
    let wall_r_top = Pos2::new(center.x + r * 0.68, center.y - r * 0.12);
    let bot_l = Pos2::new(center.x - r * 0.68, center.y + r * 0.88);
    let bot_r = Pos2::new(center.x + r * 0.68, center.y + r * 0.88);

    if filled {
        painter.add(egui::Shape::convex_polygon(
            vec![roof_top, roof_r, roof_l],
            color,
            Stroke::NONE,
        ));
        painter.rect_filled(
            Rect::from_min_max(wall_l_top, bot_r),
            CornerRadius::same(1),
            color,
        );
        // Porta vazada
        let door = Rect::from_min_max(
            Pos2::new(center.x - r * 0.24, center.y + r * 0.15),
            Pos2::new(center.x + r * 0.24, center.y + r * 0.90),
        );
        painter.rect_filled(door, CornerRadius::ZERO, Color32::from_rgb(31, 31, 31));
    } else {
        let st = Stroke::new(1.6_f32, color);
        painter.add(egui::Shape::line(vec![roof_l, roof_top, roof_r], st));
        painter.add(egui::Shape::line(vec![wall_l_top, bot_l, bot_r, wall_r_top], st));
    }
}

pub fn draw_subscriptions(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.48;
    let st = Stroke::new(1.4_f32, color);
    // 2 linhas de empilhamento no topo
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.55, center.y - r * 0.85),
            Pos2::new(center.x + r * 0.55, center.y - r * 0.85),
        ],
        st,
    );
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.78, center.y - r * 0.50),
            Pos2::new(center.x + r * 0.78, center.y - r * 0.50),
        ],
        st,
    );
    // Retângulo principal + triângulo Play
    let box_rect = Rect::from_min_max(
        Pos2::new(center.x - r * 0.95, center.y - r * 0.16),
        Pos2::new(center.x + r * 0.95, center.y + r * 0.86),
    );
    painter.rect_stroke(box_rect, CornerRadius::same(2), st, egui::StrokeKind::Inside);
    draw_play_triangle(painter, box_rect.center(), size * 0.36, color);
}

pub fn draw_history(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.44;
    let st = Stroke::new(1.6_f32, color);
    painter.circle_stroke(center, r, st);
    // Ponteiros do relógio
    painter.line_segment([center, Pos2::new(center.x, center.y - r * 0.55)], st);
    painter.line_segment(
        [center, Pos2::new(center.x + r * 0.48, center.y + r * 0.28)],
        st,
    );
}

pub fn draw_playlists(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.46;
    let st = Stroke::new(1.6_f32, color);
    for (i, w_factor) in [(0, 1.0_f32), (1, 1.0), (2, 0.55)] {
        let y = center.y - r * 0.58 + i as f32 * (r * 0.56);
        painter.line_segment(
            [
                Pos2::new(center.x - r * 0.85, y),
                Pos2::new(center.x - r * 0.85 + r * 1.5 * w_factor, y),
            ],
            st,
        );
    }
    draw_play_triangle(
        painter,
        Pos2::new(center.x + r * 0.52, center.y + r * 0.52),
        size * 0.38,
        color,
    );
}

pub fn draw_person(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.46;
    painter.circle_filled(Pos2::new(center.x, center.y - r * 0.36), r * 0.38, color);
    let body = Rect::from_min_max(
        Pos2::new(center.x - r * 0.72, center.y + r * 0.16),
        Pos2::new(center.x + r * 0.72, center.y + r * 0.85),
    );
    painter.rect_filled(body, CornerRadius::same(6), color);
}

pub fn draw_link(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.48;
    let st = Stroke::new(1.6_f32, color);
    let left = Rect::from_min_max(
        Pos2::new(center.x - r * 0.92, center.y - r * 0.38),
        Pos2::new(center.x - r * 0.06, center.y + r * 0.38),
    );
    let right = Rect::from_min_max(
        Pos2::new(center.x + r * 0.06, center.y - r * 0.38),
        Pos2::new(center.x + r * 0.92, center.y + r * 0.38),
    );
    painter.rect_stroke(left, CornerRadius::same(5), st, egui::StrokeKind::Inside);
    painter.rect_stroke(right, CornerRadius::same(5), st, egui::StrokeKind::Inside);
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.35, center.y),
            Pos2::new(center.x + r * 0.35, center.y),
        ],
        st,
    );
}

pub fn draw_explore(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.45;
    painter.circle_stroke(center, r, Stroke::new(1.6_f32, color));
    let pts = vec![
        Pos2::new(center.x + r * 0.55, center.y - r * 0.55),
        Pos2::new(center.x + r * 0.16, center.y + r * 0.16),
        Pos2::new(center.x - r * 0.55, center.y + r * 0.55),
        Pos2::new(center.x - r * 0.16, center.y - r * 0.16),
    ];
    painter.add(egui::Shape::convex_polygon(pts, color, Stroke::NONE));
}

pub fn draw_heart(painter: &egui::Painter, center: Pos2, size: f32, color: Color32, filled: bool) {
    let r = size * 0.45;
    let c_l = Pos2::new(center.x - r * 0.42, center.y - r * 0.22);
    let c_r = Pos2::new(center.x + r * 0.42, center.y - r * 0.22);
    let tip = Pos2::new(center.x, center.y + r * 0.85);
    if filled {
        painter.circle_filled(c_l, r * 0.46, color);
        painter.circle_filled(c_r, r * 0.46, color);
        painter.add(egui::Shape::convex_polygon(
            vec![
                Pos2::new(center.x - r * 0.84, center.y - r * 0.05),
                Pos2::new(center.x + r * 0.84, center.y - r * 0.05),
                tip,
            ],
            color,
            Stroke::NONE,
        ));
    } else {
        let st = Stroke::new(1.6_f32, color);
        painter.add(egui::Shape::line(
            vec![
                Pos2::new(center.x, center.y - r * 0.35),
                Pos2::new(center.x - r * 0.45, center.y - r * 0.65),
                Pos2::new(center.x - r * 0.85, center.y - r * 0.20),
                tip,
                Pos2::new(center.x + r * 0.85, center.y - r * 0.20),
                Pos2::new(center.x + r * 0.45, center.y - r * 0.65),
                Pos2::new(center.x, center.y - r * 0.35),
            ],
            st,
        ));
    }
}

pub fn draw_queue(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.46;
    let st = Stroke::new(1.6_f32, color);
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.85, center.y - r * 0.55),
            Pos2::new(center.x + r * 0.45, center.y - r * 0.55),
        ],
        st,
    );
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.85, center.y),
            Pos2::new(center.x + r * 0.45, center.y),
        ],
        st,
    );
    painter.line_segment(
        [
            Pos2::new(center.x - r * 0.85, center.y + r * 0.55),
            Pos2::new(center.x + r * 0.05, center.y + r * 0.55),
        ],
        st,
    );
    // Nota musical à direita
    painter.circle_filled(Pos2::new(center.x + r * 0.52, center.y + r * 0.56), r * 0.26, color);
    painter.line_segment(
        [
            Pos2::new(center.x + r * 0.72, center.y + r * 0.52),
            Pos2::new(center.x + r * 0.72, center.y - r * 0.42),
        ],
        st,
    );
}

pub fn draw_speaker(painter: &egui::Painter, center: Pos2, size: f32, color: Color32, muted: bool) {
    let r = size * 0.46;
    let box_r = Rect::from_min_max(
        Pos2::new(center.x - r * 0.85, center.y - r * 0.32),
        Pos2::new(center.x - r * 0.35, center.y + r * 0.32),
    );
    painter.rect_filled(box_r, CornerRadius::same(1), color);
    painter.add(egui::Shape::convex_polygon(
        vec![
            Pos2::new(center.x - r * 0.42, center.y - r * 0.32),
            Pos2::new(center.x + r * 0.14, center.y - r * 0.76),
            Pos2::new(center.x + r * 0.14, center.y + r * 0.76),
            Pos2::new(center.x - r * 0.42, center.y + r * 0.32),
        ],
        color,
        Stroke::NONE,
    ));

    if muted {
        let st = Stroke::new(1.8_f32, Color32::from_rgb(255, 82, 82));
        painter.line_segment(
            [
                Pos2::new(center.x + r * 0.32, center.y - r * 0.38),
                Pos2::new(center.x + r * 0.92, center.y + r * 0.38),
            ],
            st,
        );
        painter.line_segment(
            [
                Pos2::new(center.x + r * 0.92, center.y - r * 0.38),
                Pos2::new(center.x + r * 0.32, center.y + r * 0.38),
            ],
            st,
        );
    } else {
        let st = Stroke::new(1.5_f32, color);
        for rad in [r * 0.46, r * 0.78] {
            let mut arc = Vec::with_capacity(8);
            for i in 0..=7 {
                let a = -0.78_f32 + (i as f32 / 7.0) * 1.56_f32;
                arc.push(Pos2::new(
                    center.x + r * 0.15 + rad * a.cos(),
                    center.y + rad * a.sin(),
                ));
            }
            painter.add(egui::Shape::line(arc, st));
        }
    }
}

pub fn draw_shuffle(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.46;
    let st = Stroke::new(1.65_f32, color);
    let y_top = center.y - r * 0.48;
    let y_bot = center.y + r * 0.48;
    let x_l = center.x - r * 0.88;
    let x_m1 = center.x - r * 0.28;
    let x_m2 = center.x + r * 0.26;
    let x_r = center.x + r * 0.72;
    let tip_x = center.x + r * 0.94;
    let ah = r * 0.30;

    // Caminho 1 (superior-esquerda -> inferior-direita) com seta à direita
    painter.add(egui::Shape::line(
        vec![
            Pos2::new(x_l, y_top),
            Pos2::new(x_m1, y_top),
            Pos2::new(x_m2, y_bot),
            Pos2::new(x_r, y_bot),
        ],
        st,
    ));
    painter.add(egui::Shape::convex_polygon(
        vec![
            Pos2::new(tip_x, y_bot),
            Pos2::new(x_r - r * 0.08, y_bot - ah),
            Pos2::new(x_r - r * 0.08, y_bot + ah),
        ],
        color,
        Stroke::NONE,
    ));

    // Caminho 2 (inferior-esquerda -> superior-direita) com seta à direita
    painter.add(egui::Shape::line(
        vec![
            Pos2::new(x_l, y_bot),
            Pos2::new(x_m1, y_bot),
            Pos2::new(x_m2, y_top),
            Pos2::new(x_r, y_top),
        ],
        st,
    ));
    painter.add(egui::Shape::convex_polygon(
        vec![
            Pos2::new(tip_x, y_top),
            Pos2::new(x_r - r * 0.08, y_top - ah),
            Pos2::new(x_r - r * 0.08, y_top + ah),
        ],
        color,
        Stroke::NONE,
    ));
}

pub fn draw_repeat(painter: &egui::Painter, center: Pos2, size: f32, color: Color32, repeat_one: bool) {
    let r = size * 0.46;
    let st = Stroke::new(1.65_f32, color);
    let y_top = center.y - r * 0.50;
    let y_bot = center.y + r * 0.50;
    let x_l = center.x - r * 0.78;
    let x_r = center.x + r * 0.78;
    let cr = r * 0.28;
    let ah = r * 0.30;

    // Arco superior: sobe à esquerda -> segue para a direita -> ponta de seta para a direita
    painter.add(egui::Shape::line(
        vec![
            Pos2::new(x_l, center.y - r * 0.06),
            Pos2::new(x_l, y_top + cr),
            Pos2::new(x_l + cr, y_top),
            Pos2::new(x_r - r * 0.08, y_top),
        ],
        st,
    ));
    painter.add(egui::Shape::convex_polygon(
        vec![
            Pos2::new(x_r + r * 0.16, y_top),
            Pos2::new(x_r - r * 0.22, y_top - ah),
            Pos2::new(x_r - r * 0.22, y_top + ah),
        ],
        color,
        Stroke::NONE,
    ));

    // Arco inferior: desce à direita -> segue para a esquerda -> ponta de seta para a esquerda
    painter.add(egui::Shape::line(
        vec![
            Pos2::new(x_r, center.y + r * 0.06),
            Pos2::new(x_r, y_bot - cr),
            Pos2::new(x_r - cr, y_bot),
            Pos2::new(x_l + r * 0.08, y_bot),
        ],
        st,
    ));
    painter.add(egui::Shape::convex_polygon(
        vec![
            Pos2::new(x_l - r * 0.16, y_bot),
            Pos2::new(x_l + r * 0.22, y_bot - ah),
            Pos2::new(x_l + r * 0.22, y_bot + ah),
        ],
        color,
        Stroke::NONE,
    ));

    // "1" limpo centralizado dentro do loop quando em modo Repetir Uma
    if repeat_one {
        painter.text(
            Pos2::new(center.x, center.y + 0.5),
            egui::Align2::CENTER_CENTER,
            "1",
            egui::FontId::new((size * 0.50).max(8.5), egui::FontFamily::Proportional),
            color,
        );
    }
}

pub fn draw_gear(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.34;
    painter.circle_stroke(center, r, Stroke::new(2.0_f32, color));
    let st = Stroke::new(2.0_f32, color);
    for i in 0..8 {
        let a = (i as f32) * std::f32::consts::FRAC_PI_4;
        painter.line_segment(
            [
                Pos2::new(center.x + (r * 0.95) * a.cos(), center.y + (r * 0.95) * a.sin()),
                Pos2::new(center.x + (r * 1.48) * a.cos(), center.y + (r * 1.48) * a.sin()),
            ],
            st,
        );
    }
}

pub fn draw_fullscreen(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let r = size * 0.42;
    let arm = r * 0.58;
    let st = Stroke::new(1.8_f32, color);
    // 4 cantos [ ]
    let corners = [
        (Pos2::new(center.x - r, center.y - r), 1.0_f32, 1.0_f32),
        (Pos2::new(center.x + r, center.y - r), -1.0_f32, 1.0_f32),
        (Pos2::new(center.x - r, center.y + r), 1.0_f32, -1.0_f32),
        (Pos2::new(center.x + r, center.y + r), -1.0_f32, -1.0_f32),
    ];
    for (c, dx, dy) in corners {
        painter.line_segment([c, Pos2::new(c.x + dx * arm, c.y)], st);
        painter.line_segment([c, Pos2::new(c.x, c.y + dy * arm)], st);
    }
}

pub fn draw_theater_mode(
    painter: &egui::Painter,
    center: Pos2,
    size: f32,
    color: Color32,
    is_theater: bool,
) {
    let st = Stroke::new(1.7_f32, color);
    if is_theater {
        // Ícone "Visualização padrão" (retângulo mais compacto centralizado)
        let r = Rect::from_center_size(center, Vec2::new(size * 0.90, size * 0.62));
        painter.rect_stroke(r, CornerRadius::same(2), st, egui::StrokeKind::Inside);
        let inner = Rect::from_min_max(
            Pos2::new(r.left() + 3.0, r.top() + 3.0),
            Pos2::new(r.right() - 5.5, r.bottom() - 3.0),
        );
        painter.rect_filled(inner, CornerRadius::same(1), color);
    } else {
        // Ícone "Modo Teatro" (tela panorâmica larga)
        let r = Rect::from_center_size(center, Vec2::new(size * 1.12, size * 0.72));
        painter.rect_stroke(r, CornerRadius::same(2), st, egui::StrokeKind::Inside);
    }
}

/// Cor de avatar determinística baseada no nome do canal (igual ao WpfMediaListControl)
pub fn channel_avatar_color(name: &str) -> Color32 {
    let palette = [
        Color32::from_rgb(0xE1, 0x1D, 0x48),
        Color32::from_rgb(0x25, 0x63, 0xEB),
        Color32::from_rgb(0x05, 0x96, 0x69),
        Color32::from_rgb(0xD9, 0x77, 0x06),
        Color32::from_rgb(0x7C, 0x3A, 0xED),
        Color32::from_rgb(0x08, 0x91, 0xB2),
        Color32::from_rgb(0xDB, 0x27, 0x77),
    ];
    let mut hash: usize = 0;
    for b in name.bytes() {
        hash = hash.wrapping_mul(31).wrapping_add(b as usize);
    }
    palette[hash % palette.len()]
}
