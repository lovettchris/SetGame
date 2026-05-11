// Set Game - SVG Card Renderer

class CardRenderer {
    /**
     * Render a card as an SVG element.
     * @param {Card} card - The card to render
     * @param {boolean} selected - Whether the card is currently selected
     * @returns {string} SVG markup string
     */
    static render(card, selected = false) {
        const width = 140;
        const height = 200;

        let borderColor = 'transparent';
        let bgColor = '#fff';
        if (selected) {
            bgColor = '#e3f2fd';
        }

        const strokeColor = CardRenderer.getColor(card.color);
        const fillInfo = CardRenderer.getFill(card.shading, card.color, card.id);

        // Position shapes vertically centered
        const shapes = CardRenderer.getShapeSVGs(card.number, card.shape, fillInfo, strokeColor, width, height);

        return `
            <svg viewBox="0 0 ${width} ${height}" class="card-svg" xmlns="http://www.w3.org/2000/svg">
                <defs>
                    ${fillInfo.defs}
                </defs>
                <rect x="2" y="2" width="${width - 4}" height="${height - 4}"
                      rx="10" ry="10" fill="${bgColor}" stroke="${borderColor}" stroke-width="3"/>
                ${shapes}
            </svg>
        `;
    }

    static getColor(color) {
        switch (color) {
            case 'red': return '#e74c3c';
            case 'green': return '#27ae60';
            case 'purple': return '#6a3dba';
            default: return '#000';
        }
    }

    /**
     * Get fill style and pattern definitions for the shading type.
     */
    static getFill(shading, color, cardId) {
        const strokeColor = CardRenderer.getColor(color);
        switch (shading) {
            case 'solid':
                return {
                    fill: strokeColor,
                    defs: ''
                };
            case 'striped': {
                const patternId = `stripe-${cardId}`;
                return {
                    fill: `url(#${patternId})`,
                    defs: `
                        <pattern id="${patternId}" patternUnits="userSpaceOnUse"
                                 width="4" height="4" patternTransform="rotate(0)">
                            <rect width="4" height="1" fill="${strokeColor}" shape-rendering="crispEdges"/>
                        </pattern>
                    `
                };
            }
            case 'open':
                return {
                    fill: 'transparent',
                    defs: ''
                };
            default:
                return { fill: 'transparent', defs: '' };
        }
    }

    /**
     * Generate positioned shape SVGs for the card.
     */
    static getShapeSVGs(number, shape, fillInfo, strokeColor, cardW, cardH) {
        const shapeW = 80;
        const shapeH = 36;
        const gap = 8;
        const totalH = number * shapeH + (number - 1) * gap;
        const startY = (cardH - totalH) / 2;
        const cx = cardW / 2;

        let svg = '';
        for (let i = 0; i < number; i++) {
            const y = startY + i * (shapeH + gap);
            svg += CardRenderer.drawShape(shape, cx, y, shapeW, shapeH, fillInfo.fill, strokeColor);
        }
        return svg;
    }

    /**
     * Draw a single shape centered at (cx, y) with given dimensions.
     */
    static drawShape(shape, cx, y, w, h, fill, stroke) {
        const x = cx - w / 2;
        const midY = y + h / 2;

        switch (shape) {
            case 'diamond':
                return CardRenderer.drawDiamond(cx, midY, w, h, fill, stroke);
            case 'oval':
                return CardRenderer.drawOval(cx, midY, w, h, fill, stroke);
            case 'squiggle':
                return CardRenderer.drawSquiggle(cx, midY, w, h, fill, stroke);
            default:
                return '';
        }
    }

    static drawDiamond(cx, cy, w, h, fill, stroke) {
        const hw = w / 2;
        const hh = h / 2;
        const points = [
            `${cx},${cy - hh}`,
            `${cx + hw},${cy}`,
            `${cx},${cy + hh}`,
            `${cx - hw},${cy}`
        ].join(' ');

        return `<polygon points="${points}" fill="${fill}" stroke="${stroke}" stroke-width="2.5"/>`;
    }

    static drawOval(cx, cy, w, h, fill, stroke) {
        const x = cx - w / 2;
        const y = cy - h / 2;
        const r = h / 2;
        return `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${r}" ry="${r}"
                    fill="${fill}" stroke="${stroke}" stroke-width="2.5"/>`;
    }

    static drawSquiggle(cx, cy, w, h, fill, stroke) {
        // Squiggle shape from reference coordinates (181x77 source), smoothed with cubic bezier
        const rawPoints = [
            [1,56],[4,39],[14,25],[28,15],[44,10],[65,8],[86,11],[105,17],
            [126,16],[145,8],[162,1],[175,8],[179,21],[178,37],[171,53],
            [159,64],[145,70],[124,71],[107,67],[90,62],[73,58],[51,61],
            [35,67],[17,76],[6,70]
        ];
        const srcW = 181, srcH = 77;
        const scaleX = w / srcW;
        const scaleY = h / srcH;
        const ox = cx - w / 2;
        const oy = cy - h / 2;

        // Scale points
        const pts = rawPoints.map(([x, y]) => [ox + x * scaleX, oy + y * scaleY]);

        // Build a closed smooth cubic bezier path through the points using Catmull-Rom to Bezier conversion
        const n = pts.length;
        const d = [`M ${pts[0][0]},${pts[0][1]}`];

        for (let i = 0; i < n; i++) {
            const p0 = pts[(i - 1 + n) % n];
            const p1 = pts[i];
            const p2 = pts[(i + 1) % n];
            const p3 = pts[(i + 2) % n];

            // Catmull-Rom to cubic bezier control points (tension = 1/6)
            const cp1x = p1[0] + (p2[0] - p0[0]) / 6;
            const cp1y = p1[1] + (p2[1] - p0[1]) / 6;
            const cp2x = p2[0] - (p3[0] - p1[0]) / 6;
            const cp2y = p2[1] - (p3[1] - p1[1]) / 6;

            d.push(`C ${cp1x},${cp1y} ${cp2x},${cp2y} ${p2[0]},${p2[1]}`);
        }

        d.push('Z');

        return `<path d="${d.join(' ')}" fill="${fill}" stroke="${stroke}" stroke-width="2.5"/>`;
    }
}
