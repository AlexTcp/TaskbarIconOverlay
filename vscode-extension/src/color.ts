// Colour parsing compatible with the inputs Peacock accepts (Peacock uses tinycolor2):
// hex (#rgb, #rgba, #rrggbb, #rrggbbaa, with or without '#'), rgb()/rgba(), hsl()/hsla(),
// hsv()/hsva() and CSS named colours. Everything is normalized to lowercase "#rrggbb".

export interface Rgb {
  r: number;
  g: number;
  b: number;
}

export interface Hsl {
  h: number; // 0..360
  s: number; // 0..1
  l: number; // 0..1
}

const NAMED_COLORS: Record<string, string> = {
  aliceblue: 'f0f8ff', antiquewhite: 'faebd7', aqua: '00ffff', aquamarine: '7fffd4', azure: 'f0ffff',
  beige: 'f5f5dc', bisque: 'ffe4c4', black: '000000', blanchedalmond: 'ffebcd', blue: '0000ff',
  blueviolet: '8a2be2', brown: 'a52a2a', burlywood: 'deb887', burntsienna: 'ea7e5d', cadetblue: '5f9ea0',
  chartreuse: '7fff00', chocolate: 'd2691e', coral: 'ff7f50', cornflowerblue: '6495ed', cornsilk: 'fff8dc',
  crimson: 'dc143c', cyan: '00ffff', darkblue: '00008b', darkcyan: '008b8b', darkgoldenrod: 'b8860b',
  darkgray: 'a9a9a9', darkgreen: '006400', darkgrey: 'a9a9a9', darkkhaki: 'bdb76b', darkmagenta: '8b008b',
  darkolivegreen: '556b2f', darkorange: 'ff8c00', darkorchid: '9932cc', darkred: '8b0000', darksalmon: 'e9967a',
  darkseagreen: '8fbc8f', darkslateblue: '483d8b', darkslategray: '2f4f4f', darkslategrey: '2f4f4f',
  darkturquoise: '00ced1', darkviolet: '9400d3', deeppink: 'ff1493', deepskyblue: '00bfff', dimgray: '696969',
  dimgrey: '696969', dodgerblue: '1e90ff', firebrick: 'b22222', floralwhite: 'fffaf0', forestgreen: '228b22',
  fuchsia: 'ff00ff', gainsboro: 'dcdcdc', ghostwhite: 'f8f8ff', gold: 'ffd700', goldenrod: 'daa520',
  gray: '808080', green: '008000', greenyellow: 'adff2f', grey: '808080', honeydew: 'f0fff0',
  hotpink: 'ff69b4', indianred: 'cd5c5c', indigo: '4b0082', ivory: 'fffff0', khaki: 'f0e68c',
  lavender: 'e6e6fa', lavenderblush: 'fff0f5', lawngreen: '7cfc00', lemonchiffon: 'fffacd', lightblue: 'add8e6',
  lightcoral: 'f08080', lightcyan: 'e0ffff', lightgoldenrodyellow: 'fafad2', lightgray: 'd3d3d3',
  lightgreen: '90ee90', lightgrey: 'd3d3d3', lightpink: 'ffb6c1', lightsalmon: 'ffa07a', lightseagreen: '20b2aa',
  lightskyblue: '87cefa', lightslategray: '778899', lightslategrey: '778899', lightsteelblue: 'b0c4de',
  lightyellow: 'ffffe0', lime: '00ff00', limegreen: '32cd32', linen: 'faf0e6', magenta: 'ff00ff',
  maroon: '800000', mediumaquamarine: '66cdaa', mediumblue: '0000cd', mediumorchid: 'ba55d3',
  mediumpurple: '9370db', mediumseagreen: '3cb371', mediumslateblue: '7b68ee', mediumspringgreen: '00fa9a',
  mediumturquoise: '48d1cc', mediumvioletred: 'c71585', midnightblue: '191970', mintcream: 'f5fffa',
  mistyrose: 'ffe4e1', moccasin: 'ffe4b5', navajowhite: 'ffdead', navy: '000080', oldlace: 'fdf5e6',
  olive: '808000', olivedrab: '6b8e23', orange: 'ffa500', orangered: 'ff4500', orchid: 'da70d6',
  palegoldenrod: 'eee8aa', palegreen: '98fb98', paleturquoise: 'afeeee', palevioletred: 'db7093',
  papayawhip: 'ffefd5', peachpuff: 'ffdab9', peru: 'cd853f', pink: 'ffc0cb', plum: 'dda0dd',
  powderblue: 'b0e0e6', purple: '800080', rebeccapurple: '663399', red: 'ff0000', rosybrown: 'bc8f8f',
  royalblue: '4169e1', saddlebrown: '8b4513', salmon: 'fa8072', sandybrown: 'f4a460', seagreen: '2e8b57',
  seashell: 'fff5ee', sienna: 'a0522d', silver: 'c0c0c0', skyblue: '87ceeb', slateblue: '6a5acd',
  slategray: '708090', slategrey: '708090', snow: 'fffafa', springgreen: '00ff7f', steelblue: '4682b4',
  tan: 'd2b48c', teal: '008080', thistle: 'd8bfd8', tomato: 'ff6347', turquoise: '40e0d0', violet: 'ee82ee',
  wheat: 'f5deb3', white: 'ffffff', whitesmoke: 'f5f5f5', yellow: 'ffff00', yellowgreen: '9acd32',
};

const NUMBER = '[-\\+]?\\d*\\.?\\d+%?';
const SEP = '[\\s,]+';
const FUNC3 = new RegExp(`^(rgb|hsl|hsv)a?\\(\\s*(${NUMBER})${SEP}(${NUMBER})${SEP}(${NUMBER})(?:\\s*[,/]\\s*(${NUMBER}))?\\s*\\)$`);
const FUNC3_BARE = new RegExp(`^(rgb|hsl|hsv)a?\\s+(${NUMBER})${SEP}(${NUMBER})${SEP}(${NUMBER})(?:${SEP}(${NUMBER}))?$`);
const HEX = /^#?([0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$/;

/** Parses a colour string. Returns null for invalid or fully transparent input. */
export function parseColor(input: unknown): Rgb | null {
  if (typeof input !== 'string') return null;
  const text = input.trim().toLowerCase();
  if (!text) return null;

  if (text === 'transparent') return null;

  const named = NAMED_COLORS[text];
  if (named) return hexToRgb(named);

  const hex = HEX.exec(text);
  if (hex) {
    let digits = hex[1];
    if (digits.length === 3 || digits.length === 4) {
      digits = digits.split('').map(c => c + c).join('');
    }
    if (digits.length === 8 && parseInt(digits.slice(6, 8), 16) === 0) return null;
    return hexToRgb(digits.slice(0, 6));
  }

  const fn = FUNC3.exec(text) || FUNC3_BARE.exec(text);
  if (fn) {
    const [, kind, a, b, c, alpha] = fn;
    if (alpha !== undefined && parseUnit(alpha, 1) === 0) return null;
    if (kind === 'rgb') {
      return {
        r: clampByte(parseUnit(a, 255)),
        g: clampByte(parseUnit(b, 255)),
        b: clampByte(parseUnit(c, 255)),
      };
    }
    const h = parseFloat(a);
    const s = clamp01(parseUnit(b, 1, true));
    const v = clamp01(parseUnit(c, 1, true));
    return kind === 'hsl' ? hslToRgb({ h, s, l: v }) : hsvToRgb(h, s, v);
  }

  return null;
}

/** Normalizes a colour string to lowercase "#rrggbb", or null when invalid. */
export function normalizeColor(input: unknown): string | null {
  const rgb = parseColor(input);
  return rgb ? rgbToHex(rgb) : null;
}

export function rgbToHex({ r, g, b }: Rgb): string {
  return '#' + [r, g, b].map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
}

export function hexToRgb(hex: string): Rgb {
  const h = hex.replace('#', '');
  return {
    r: parseInt(h.slice(0, 2), 16),
    g: parseInt(h.slice(2, 4), 16),
    b: parseInt(h.slice(4, 6), 16),
  };
}

export function rgbToHsl({ r, g, b }: Rgb): Hsl {
  const rn = r / 255, gn = g / 255, bn = b / 255;
  const max = Math.max(rn, gn, bn), min = Math.min(rn, gn, bn);
  const l = (max + min) / 2;
  if (max === min) return { h: 0, s: 0, l };
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h: number;
  if (max === rn) h = (gn - bn) / d + (gn < bn ? 6 : 0);
  else if (max === gn) h = (bn - rn) / d + 2;
  else h = (rn - gn) / d + 4;
  return { h: h * 60, s, l };
}

export function hslToRgb({ h, s, l }: Hsl): Rgb {
  const hn = (((h % 360) + 360) % 360) / 360;
  if (s === 0) {
    const v = clampByte(l * 255);
    return { r: v, g: v, b: v };
  }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  const hue = (t: number) => {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  return {
    r: clampByte(hue(hn + 1 / 3) * 255),
    g: clampByte(hue(hn) * 255),
    b: clampByte(hue(hn - 1 / 3) * 255),
  };
}

/** Same semantics as tinycolor's lighten()/darken(): shifts HSL lightness by amount percent. */
export function adjustLightness(hex: string, amountPercent: number): string {
  const hsl = rgbToHsl(hexToRgb(hex));
  hsl.l = clamp01(hsl.l + amountPercent / 100);
  return rgbToHex(hslToRgb(hsl));
}

function hsvToRgb(h: number, s: number, v: number): Rgb {
  const hn = ((((h % 360) + 360) % 360) / 360) * 6;
  const i = Math.floor(hn);
  const f = hn - i;
  const p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
  const mod = i % 6;
  const r = [v, q, p, p, t, v][mod], g = [t, v, v, q, p, p][mod], b = [p, p, t, v, v, q][mod];
  return { r: clampByte(r * 255), g: clampByte(g * 255), b: clampByte(b * 255) };
}

// tinycolor semantics: "50%" is a percentage of max; a bare number <= 1 for s/l/v/alpha is a fraction.
function parseUnit(value: string, max: number, fractionAllowed = false): number {
  if (value.endsWith('%')) return (parseFloat(value) / 100) * max;
  const n = parseFloat(value);
  if (max === 1 && !fractionAllowed) return n; // alpha
  if (fractionAllowed && n <= 1) return n;
  return fractionAllowed ? n / 100 : n;
}

function clampByte(v: number): number {
  return Math.max(0, Math.min(255, Math.round(v)));
}

function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}
