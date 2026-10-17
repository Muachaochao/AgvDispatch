// Derived from medicalprogrammer/agv-web-visible, MIT. See LICENSE and UPSTREAM.md.
import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
type IPoint = { x: number; y?: number; z: number; stay?: number };

export interface BaseMesh {
  name: string;
  type: string;
  x: number;
  y: number;
  z: number;
  width?: number;
  height?: number;
  depth?: number;
  size?: number;
  color?: string;
  layer?: number;
  angle?: number;
  modelUrl?: string;
}

/**
 * 创建文字标签
 * @param element
 */
export const makeLabel = (element: BaseMesh): THREE.Sprite => {
  const { name, x, z } = element;
  const sprite = makeTextSprite(name, {
    color: "#00ee88",
    fontSize: 48,
    bgColor: "rgba(0,0,0,0.4)", // 半透明背景，可选
  });
  sprite.position.set(x, 1.8, z); // 文字悬停高度可自己调
  return sprite;
};

/**
 * 创建航点
 * @param element
 */
export const makePoint = (element: BaseMesh): THREE.Group => {
  const { name, x, z, color } = element;
  const group = new THREE.Group();
  group.name = name;

  const geo = new THREE.SphereGeometry(0.25, 16, 16);
  const mat = new THREE.MeshStandardMaterial({
    color: color ?? 0x00cccc,
    roughness: 0.5,
  });
  const sphere = new THREE.Mesh(geo, mat);
  const sphereY = 0; // 顶部刚好 0.5
  sphere.position.set(x, sphereY, z); // 中心仍在 0.25
  group.add(sphere);

  // 文字标签
  const label = makeTextSprite(name, { color, fontSize: 40 });
  label.position.set(x, sphereY + 0.6, z); // 0.4 是标签视觉高度的一半，可微调
  group.add(label);

  return group;
};

const modelCache = new Map();
const loadModel = async (url: string): Promise<THREE.Group> => {
  if (modelCache.has(url)) {
    return modelCache.get(url).clone();
  }

  const loader = new GLTFLoader();
  const gltf = await loader.loadAsync(url);
  const model = gltf.scene;
  modelCache.set(url, model);
  return model.clone(); // 返回克隆的模型
};

/**
 * 创建货架
 * @param element
 */
export const makeShelf = async (element: BaseMesh): Promise<THREE.Group> => {
  const { name, x, z, width, height, depth, size, angle } = element;

  const group = new THREE.Group();
  group.name = name;
  group.position.set(x, 0, z);
  group.rotateY(THREE.MathUtils.degToRad(angle ?? 0));

  const modelUrl = "/model/shelf.glb";
  const shelf = await loadModel(modelUrl);
  const scalar = size ?? 1;
  shelf.scale.setScalar(scalar);
  shelf.scale.set(width ?? 1, depth ?? 1, height ?? 1);
  group.add(shelf);

  const label = makeTextSprite(name, { color: "#00BFFF", fontSize: 26 });
  label.position.set(0, 1, 0); // 悬在区域上方
  group.add(label);

  return group;
};

/**
 * 创建模型
 * @param element
 */
export const makeModel = async (element: BaseMesh): Promise<THREE.Group> => {
  const { name, x, z, size } = element;

  const group = new THREE.Group();
  group.name = name;
  group.position.set(x, 0, z);

  const shelf = await loadModel(element.modelUrl ?? "/model/pallet.gltf");
  const scalar = size ?? 1;
  shelf.scale.setScalar(scalar);
  group.add(shelf);

  const label = makeTextSprite(name, { color: "#00BFFF", fontSize: 26 });
  label.position.set(0, 1, 0); // 悬在区域上方
  group.add(label);

  return group;
};

/**
 * 创建墙
 * @param element
 */
export const makeWall = async (element: BaseMesh): Promise<THREE.Group> => {
  const { name, x, z, width, height, depth, size, angle } = element;

  const group = new THREE.Group();
  group.name = name;
  group.position.set(x, 0, z);
  group.rotateY(THREE.MathUtils.degToRad(angle ?? 0));

  const modelUrl = "/model/wall.glb";
  const wall = await loadModel(modelUrl);
  const scalar = size ?? 1;
  wall.scale.setScalar(scalar);
  wall.scale.set(width ?? 1, depth ?? 1, height ?? 1);
  group.add(wall);

  const label = makeTextSprite(name, { color: "#00BFFF", fontSize: 26 });
  label.position.set(0, 1, 0); // 悬在区域上方
  group.add(label);

  return group;
};

/**
 * 创建停靠区
 * @param element
 */
export const makeWaitingArea = (element: BaseMesh): THREE.Group => {
  const { name, x, z, width, height, angle } = element;
  const group = new THREE.Group();
  group.name = name;
  group.rotateY(THREE.MathUtils.degToRad(angle ?? 0));

  // 2. 平面几何（中心点模式）
  const geo = new THREE.PlaneGeometry(width, height);
  geo.rotateX(-Math.PI / 2); // 地面朝向

  const mat = new THREE.MeshStandardMaterial({
    color: 0x00ff88, // 亮绿色
    transparent: true,
    opacity: 0.35,
    side: THREE.DoubleSide,
  });
  const plane = new THREE.Mesh(geo, mat);
  plane.position.set(x, 0.01, z); // 比地面高 3 cm
  group.add(plane);

  // 3. 边框线框
  const edges = new THREE.EdgesGeometry(geo);
  const lineMat = new THREE.LineBasicMaterial({
    color: 0x00aa55,
    linewidth: 2,
  });
  const border = new THREE.LineSegments(edges, lineMat);
  border.position.copy(plane.position);
  group.add(border);

  // 4. 可选：文字标签
  if (name) {
    const label = makeTextSprite(name, { color: "#008B00", fontSize: 26 });
    label.position.set(x, 0.5, z); // 悬在区域上方
    group.add(label);
  }

  return group;
};

const PATH_POINT_RADIUS = 0.01;
/**
 * 创建线条
 * @param densePath
 * @param color
 */
export const makePathObject = (
  name: string,
  densePath: Array<IPoint>,
  color = 0x00ffcc
): THREE.Group => {
  const cnt = densePath.length;
  if (!cnt) return new THREE.Group();

  /* 1. 线 */
  const lineGeo = new THREE.BufferGeometry();
  const posArray = new Float32Array(cnt * 3);
  densePath.forEach((p, i) => {
    posArray[i * 3] = p.x;
    posArray[i * 3 + 1] = 0.01;
    posArray[i * 3 + 2] = p.z;
  });
  lineGeo.setAttribute("position", new THREE.BufferAttribute(posArray, 3));
  const lineMat = new THREE.LineBasicMaterial({ color, linewidth: 2 });
  const line = new THREE.Line(lineGeo, lineMat);

  /* 2. 点（Instanced） */
  const sphereGeo = new THREE.SphereGeometry(PATH_POINT_RADIUS, 16, 16);
  const pointMat = new THREE.MeshBasicMaterial({ color: 0xff8800 });
  const points = new THREE.InstancedMesh(sphereGeo, pointMat, cnt);
  const dummy = new THREE.Object3D();
  densePath.forEach((p, i) => {
    dummy.position.set(p.x, 0.01, p.z);
    dummy.updateMatrix();
    points.setMatrixAt(i, dummy.matrix);
  });

  const group = new THREE.Group();
  group.name = name;
  group.add(line, points);
  return group;
};

interface textSprite {
  color?: string;
  fontSize?: number;
  bgColor?: string;
}

/* ---------- 生成文字 Sprite（带可选背景） ---------- */
const makeTextSprite = (text: string, textSprite: textSprite): THREE.Sprite => {
  const color = textSprite.color ? textSprite.color : "#000000";
  const fontSize = textSprite.fontSize ? textSprite.fontSize : 32;
  const pad = 8; // 边距
  const canvas = document.createElement("canvas");
  const ctx = canvas.getContext("2d");
  if (ctx) {
    ctx.font = `bold ${fontSize}px Arial`;

    // 计算文字尺寸
    const txtWidth = ctx.measureText(text).width;
    const txtHeight = fontSize;
    canvas.width = txtWidth + pad * 2;
    canvas.height = txtHeight + pad * 2;

    // 背景
    if (textSprite.bgColor) {
      ctx.fillStyle = textSprite.bgColor;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
    }

    // 文字
    ctx.fillStyle = color;
    ctx.font = `bold ${fontSize}px Arial`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(text, canvas.width / 2, canvas.height / 2);
  }
  const texture = new THREE.CanvasTexture(canvas);
  const mat = new THREE.SpriteMaterial({ map: texture, depthTest: false });
  const sprite = new THREE.Sprite(mat);
  sprite.scale.set(canvas.width / 64, canvas.height / 64, 1);
  return sprite;
};
