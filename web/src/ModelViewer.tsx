import { useEffect, useRef, useState } from 'react'
import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import { serverAddress } from './api'
type Point = { x: number; y: number; z: number }
type Model = { version: number; vertices: number[]; triangles: number[]; colors: number[]; marker: { position: Point; normal: Point } }

export function ModelViewer({ address, id, revision }: { address: string; id: string; revision: string }) {
  const host = useRef<HTMLDivElement>(null)
  const actions = useRef<{ reset: () => void; marker: () => void; color: () => void } | null>(null)
  const [retry, setRetry] = useState(0)
  const [state, setState] = useState('正在加载模型…')
  useEffect(() => {
    const controller = new AbortController()
    let disposed = false
    let cleanup = () => {}
    const timeout = setTimeout(() => controller.abort(), 20000)
    async function load() {
      const response = await fetch(`${serverAddress(address)}/api/issues/${encodeURIComponent(id)}/model?v=${encodeURIComponent(revision)}`, { signal: controller.signal })
      if (!response.ok) throw new Error(`模型加载失败（${response.status}）`)
      const data: Model = await response.json()
      if (disposed || !host.current) return
      if (data.version !== 1 || data.vertices.length < 9 || data.vertices.length > 270000 || data.colors.length !== data.vertices.length || data.triangles.length > 480000 || !data.marker) throw new Error('模型格式无效')
      const container = host.current
      const renderer = new THREE.WebGLRenderer({ antialias: true })
      renderer.setPixelRatio(Math.min(devicePixelRatio, 2))
      container.append(renderer.domElement)
      const scene = new THREE.Scene(); scene.background = new THREE.Color('#17232c')
      const camera = new THREE.PerspectiveCamera(45, 1, 0.001, 100)
      const controls = new OrbitControls(camera, renderer.domElement)
      const geometry = new THREE.BufferGeometry()
      geometry.setAttribute('position', new THREE.Float32BufferAttribute(data.vertices, 3))
      geometry.setAttribute('color', new THREE.Float32BufferAttribute(data.colors.map(value => value / 255), 3))
      geometry.setIndex(data.triangles); geometry.computeVertexNormals(); geometry.computeBoundingSphere()
      const material = new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide })
      scene.add(new THREE.Mesh(geometry, material))
      // Apex at local zero, base 2.5 cm along outward normal; same physical marker as Unity.
      const markerGeometry = new THREE.BufferGeometry()
      markerGeometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0, -0.01, 0.025, 0.006, 0.01, 0.025, 0.006, 0, 0.025, -0.012], 3))
      markerGeometry.setIndex([0, 2, 1, 0, 3, 2, 0, 1, 3, 1, 2, 3])
      const markerMaterial = new THREE.MeshBasicMaterial({ color: '#ffda24', side: THREE.DoubleSide })
      const marker = new THREE.Mesh(markerGeometry, markerMaterial)
      const p = data.marker.position, n = data.marker.normal
      marker.position.set(p.x, p.y, p.z)
      marker.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), new THREE.Vector3(n.x, n.y, n.z).normalize())
      scene.add(marker)
      const sphere = geometry.boundingSphere!
      const reset = () => { controls.target.copy(sphere.center); camera.position.copy(sphere.center).add(new THREE.Vector3(0, 0, Math.max(sphere.radius * 3, 0.1))); controls.update() }
      reset()
      actions.current = { reset, marker: () => { marker.visible = !marker.visible }, color: () => { material.vertexColors = !material.vertexColors; material.needsUpdate = true } }
      const observer = new ResizeObserver(() => { const width = container.clientWidth; renderer.setSize(width, 360); camera.aspect = width / 360; camera.updateProjectionMatrix() })
      observer.observe(container)
      renderer.setAnimationLoop(() => { controls.update(); renderer.render(scene, camera) })
      cleanup = () => { observer.disconnect(); renderer.setAnimationLoop(null); controls.dispose(); geometry.dispose(); material.dispose(); markerGeometry.dispose(); markerMaterial.dispose(); renderer.dispose(); renderer.forceContextLoss(); renderer.domElement.remove(); actions.current = null }
      setState('')
    }
    void load().catch(error => { if (!disposed) setState(error instanceof Error ? error.message : '模型加载失败') }).finally(() => clearTimeout(timeout))
    return () => { disposed = true; controller.abort(); clearTimeout(timeout); cleanup() }
  }, [address, id, revision, retry])
  return <section aria-label="扫描模型"><p>拖动旋转 · 右键拖动平移 · 滚轮缩放，单位：米；未扫描区域保留缺口。</p>
    {state && <p role="status">{state} <button onClick={() => setRetry(value => value + 1)}>重试模型</button></p>}
    <div ref={host} style={{ width: '100%', height: 360 }} />
    <button onClick={() => actions.current?.reset()}>复位模型</button><button onClick={() => actions.current?.marker()}>显示/隐藏标记</button><button onClick={() => actions.current?.color()}>显示/隐藏颜色</button>
  </section>
}
