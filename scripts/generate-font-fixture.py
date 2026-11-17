"""生成原创三角形字形的最小 TTF，仅用于离线字体解析回归。无第三方依赖。"""
import struct
from pathlib import Path

p = lambda fmt, *v: struct.pack('>' + fmt, *v)
glyph = p('hhhhh', 1, 0, 0, 500, 700) + p('HH', 2, 0) + bytes([1, 1, 1]) + p('hhhhhh', 0, 500, -500, 0, 0, 700)
head = p('IIIIHH', 0x10000, 0x10000, 0, 0x5F0F3CF5, 0, 1000) + bytes(16) + p('hhhhHHhhh', 0, 0, 500, 700, 0, 8, 2, 1, 0)
hhea = p('IhhhHhhhhhh', 0x10000, 800, -200, 0, 600, 0, 0, 500, 1, 0, 0) + bytes(8) + p('hH', 0, 2)
fmt4 = p('HHHHHHH', 4, 32, 0, 4, 4, 1, 0) + p('HHHHHhhHH', 19968, 65535, 0, 19968, 65535, 1-19968, 1, 0, 0)
font_name = 'StudyFixture'.encode('utf-16-be')
tables = {
    'head': head, 'hhea': hhea, 'maxp': p('IH', 0x10000, 2) + bytes(26),
    'hmtx': p('HhHh', 600, 0, 600, 0), 'loca': p('III', 0, 0, len(glyph)),
    'glyf': glyph, 'cmap': p('HHHHI', 0, 1, 3, 1, 12) + fmt4,
    'name': p('HHH', 0, 1, 18) + p('HHHHHH', 3, 1, 1033, 6, len(font_name), 0) + font_name,
    'post': p('I', 0x30000) + bytes(28)
}
count = len(tables)
power = 2 ** (count.bit_length() - 1)
output = p('IHHHH', 0x10000, count, power * 16, power.bit_length() - 1, count * 16 - power * 16)
offset = 12 + count * 16
directory, contents = b'', b''
for tag, data in sorted(tables.items()):
    padded = data + bytes((-len(data)) % 4)
    checksum = sum(struct.unpack('>' + 'I' * (len(padded) // 4), padded)) & 0xFFFFFFFF
    directory += tag.encode() + p('III', checksum, offset, len(data))
    contents += padded
    offset += len(padded)
target = Path(__file__).resolve().parents[1] / 'src/Desktop.App/Assets/font-fixture.ttf'
target.write_bytes(output + directory + contents)
print(f'Generated original fixture font: {target.name}')
