import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { deleteIssue, errorMessage, listIssues, serverAddress, updateStatus, updateContent, type Issue } from './api'
import './App.css'
const ModelViewer = lazy(() => import('./ModelViewer').then(module => ({ default: module.ModelViewer })))

const priorityNames = { low: '低', medium: '中', high: '高' }
const statusNames = { open: '待处理', in_progress: '处理中', resolved: '已解决' }

function IssueCard({ issue, address, onSaved, onDeleted }: { issue: Issue; address: string; onSaved: () => void; onDeleted: () => void }) {
  const [draft, setDraft] = useState<Pick<Issue, 'title' | 'description' | 'priority'> | null>(null)
  const [expanded, setExpanded] = useState(false)
  const [failedPhoto, setFailedPhoto] = useState('')
  const [imageRetry, setImageRetry] = useState(0)
  const [saving, setSaving] = useState(false)
  const [showModel, setShowModel] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [confirmedTitle, setConfirmedTitle] = useState('')
  const operation = useRef(false)
  const photo = `${serverAddress(address)}/api/issues/${encodeURIComponent(issue.id)}/snapshot?v=${encodeURIComponent(issue.updatedAt)}&retry=${imageRetry}`
  const imageError = failedPhoto === photo

  async function saveContent(event: React.FormEvent) {
    event.preventDefault()
    if (!draft || operation.current) return
    if (!draft.title.trim()) { setError('请填写问题标题'); return }
    operation.current = true; setSaving(true); setError(''); setMessage('')
    try {
      await updateContent(address, issue.id, { ...draft, title: draft.title.trim() })
      setDraft(null); onSaved(); setMessage('内容已保存')
    } catch (reason) { setError(`保存失败：${errorMessage(reason)}。草稿已保留，可重试。`) }
    finally { operation.current = false; setSaving(false) }
  }
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  async function changeStatus(status: Issue['status']) {
    if (operation.current || status === issue.status) return
    operation.current = true
    setSaving(true)
    setError('')
    setMessage('')
    try {
      await updateStatus(address, issue.id, status)
      onSaved()
      setMessage('状态已保存')
    } catch (reason) {
      setError(`保存失败：${errorMessage(reason)}。可重试或刷新确认。`)
    } finally { operation.current = false; setSaving(false) }
  }

  async function permanentlyDelete() {
    if (operation.current || confirmedTitle !== issue.title) return
    operation.current = true; setSaving(true); setError(''); setMessage('')
    try { await deleteIssue(address, issue.id, confirmedTitle); onDeleted() }
    catch (reason) { setError(`${errorMessage(reason)}。记录和输入已保留；结果待确认时可刷新核对。`) }
    finally { operation.current = false; setSaving(false) }
  }

  return <article>
    <div className="row"><span className={`priority ${issue.priority}`}>{priorityNames[issue.priority]}优先级</span><span>{statusNames[issue.status]}</span></div>
    <h2>{issue.title}</h2><p className="id">编号：{issue.id}</p>
    <p>{issue.description || '未填写描述'}</p>
    <button onClick={() => setShowModel(false)}>现场照片</button><button onClick={() => setShowModel(true)}>三维模型</button>
    {showModel ? (issue.modelUrl ? <Suspense fallback={<p>正在加载三维查看器…</p>}><ModelViewer key={issue.id + issue.updatedAt + address} address={address} id={issue.id} revision={issue.updatedAt} /></Suspense> : <p>旧记录无模型</p>) : <>
    {issue.snapshotUrl ? <div className="snapshot">
      {imageError ? <p role="alert">图片加载失败。<button onClick={() => { setImageRetry(value => value + 1) }}>重试图片</button></p> :
        <button className="photo-button" aria-label={`${issue.title} 查看现场大图`} onClick={() => setExpanded(true)}><img src={photo} alt={`${issue.title} 现场快照`} onError={() => setFailedPhoto(photo)} /></button>}
      {expanded && <dialog open aria-label="现场大图"><button autoFocus onClick={() => setExpanded(false)}>关闭大图</button><img src={photo} alt={`${issue.title} 现场大图`} onError={() => setFailedPhoto(photo)} />{imageError && <p role="alert">图片加载失败，请关闭后重试。</p>}</dialog>}
    </div> : <p>无快照（旧记录，可在手机现场补采）</p>}
    </>}
    {draft ? <form className="content-editor" onSubmit={event => { void saveContent(event) }}>
      <label>标题<input aria-label={`${issue.title} 标题`} value={draft.title} disabled={saving} required onChange={event => setDraft({ ...draft, title: event.target.value })} /></label>
      <label>描述<textarea aria-label={`${issue.title} 描述`} value={draft.description} disabled={saving} onChange={event => setDraft({ ...draft, description: event.target.value })} /></label>
      <label>优先级<select aria-label={`${issue.title} 优先级`} value={draft.priority} disabled={saving} onChange={event => setDraft({ ...draft, priority: event.target.value as Issue['priority'] })}><option value="low">低</option><option value="medium">中</option><option value="high">高</option></select></label>
      <button disabled={saving}>保存内容</button><button type="button" disabled={saving} onClick={() => setDraft(null)}>取消编辑</button>
      <p>远端更新保留当前草稿；保存内容不会覆盖处理状态、点位或快照。</p>
    </form> : <button disabled={saving} onClick={() => { setDraft({ title: issue.title, description: issue.description, priority: issue.priority }); setError(''); setMessage('') }}>编辑内容</button>}
    <dl><dt>标记位置（米）</dt><dd>x: {issue.position.x}, y: {issue.position.y}, z: {issue.position.z}</dd><dt>创建时间</dt><dd>{new Date(issue.createdAt).toLocaleString()}</dd><dt>更新时间</dt><dd>{new Date(issue.updatedAt).toLocaleString()}</dd></dl>
    <label>处理状态 <select aria-label={`${issue.title} 处理状态`} value={issue.status} disabled={saving} onChange={event => { void changeStatus(event.target.value as Issue['status']) }}>
      <option value="open">待处理</option><option value="in_progress">处理中</option><option value="resolved">已解决</option>
    </select></label>
    <button disabled={saving} onClick={() => { setConfirmDelete(true); setConfirmedTitle(''); setError('') }}>永久删除</button>
    {confirmDelete && <dialog open className="delete-confirmation" aria-label="永久删除问题">
      <h3>永久删除“{issue.title}”</h3>
      <p>问题、现场照片及扫描模型将永久删除，无法恢复。请输入当前完整标题确认。</p>
      <label>确认标题<input aria-label="确认删除标题" value={confirmedTitle} disabled={saving} onChange={event => setConfirmedTitle(event.target.value)} /></label>
      <button disabled={saving || confirmedTitle !== issue.title} onClick={() => { void permanentlyDelete() }}>确认永久删除</button>
      <button disabled={saving} onClick={() => setConfirmDelete(false)}>取消删除</button>
      {saving && <p role="status">正在保存，请等待…</p>}
      {error && <p role="alert">{error}</p>}
    </dialog>}
    {saving && <p role="status">正在保存…</p>}
    {message && <p role="status">{message}</p>}
    {error && !confirmDelete && <p role="alert">{error}</p>}
  </article>
}

export default function App() {
  const [address, setAddress] = useState(() => import.meta.env.VITE_ARWORK_API || window.location.origin)
  const [activeAddress, setActiveAddress] = useState(address)
  const [revision, setRevision] = useState(0)
  const [issues, setIssues] = useState<Issue[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [connection, setConnection] = useState('正在连接实时通知…')

  useEffect(() => {
    let events: EventSource
    try { events = new EventSource(`${serverAddress(activeAddress)}/api/events`) }
    catch { return }
    const reload = () => setRevision(value => value + 1)
    events.addEventListener('ready', () => { setConnection('实时通知已连接'); reload() })
    events.addEventListener('issues-changed', reload)
    events.onerror = () => setConnection('实时通知已断开，正在重连；可手动刷新')
    const resume = () => { if (document.visibilityState === 'visible') reload() }
    document.addEventListener('visibilitychange', resume)
    return () => { events.close(); document.removeEventListener('visibilitychange', resume) }
  }, [activeAddress])

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    let retry: ReturnType<typeof setTimeout> | undefined
    const timeout = setTimeout(() => controller.abort(), 10000)
    listIssues(activeAddress, controller.signal)
      .then(data => { if (active) { setIssues(data); setError('') } })
      .catch((reason: unknown) => {
        if (active) {
          setError(`无法加载问题：${errorMessage(reason)}。正在重试…`)
          retry = setTimeout(() => setRevision(value => value + 1), 2000)
        }
      })
      .finally(() => { clearTimeout(timeout); if (active) setLoading(false) })
    return () => { active = false; clearTimeout(timeout); clearTimeout(retry); controller.abort() }
  }, [activeAddress, revision])

  return <main>
    <header><p className="eyebrow">园区巡检管理</p><h1>现场问题</h1><p>查看巡检问题，更新处理进度。</p></header>
    <form className="connection" onSubmit={event => {
      event.preventDefault()
      try {
        const normalized = serverAddress(address)
        localStorage.setItem('arwork.server', normalized)
        setLoading(true)
        setError('')
        setActiveAddress(normalized)
        setRevision(value => value + 1)
      } catch (reason) { setError(reason instanceof Error ? reason.message : '服务地址无效') }
    }}>
      <label htmlFor="server">后端地址</label>
      <input id="server" value={address} onChange={event => setAddress(event.target.value)} required />
      <button disabled={loading}>{loading ? '加载中…' : '连接并刷新'}</button>
    </form>
    <p role="status">{connection}</p>
    {error && <section role="alert"><strong>{error}</strong><p>确认 Go 服务已启动、地址正确且允许浏览器跨域访问。</p><button onClick={() => { setLoading(true); setError(''); setRevision(value => value + 1) }} disabled={loading}>重试</button></section>}
    {loading && <p role="status">正在加载问题…</p>}
    {!loading && !error && issues.length === 0 && <p className="empty">暂无问题。请先在手机上选点并提交，再点击刷新。</p>}
    {!loading && !error && <section aria-label="问题列表" className="issues">{issues.map(issue => <IssueCard key={`${activeAddress}/${issue.id}`} issue={issue} address={activeAddress} onSaved={() => setRevision(value => value + 1)} onDeleted={() => { setIssues(current => current.filter(item => item.id !== issue.id)); setRevision(value => value + 1) }} />)}</section>}
  </main>
}
