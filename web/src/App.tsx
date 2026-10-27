import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from 'react'
import { deleteIssue, errorMessage, listIssues, serverAddress, updateStatus, updateContent, type Issue } from './api'
import './App.css'
const ModelViewer = lazy(() => import('./ModelViewer').then(module => ({ default: module.ModelViewer })))

const priorityNames = { low: '低', medium: '中', high: '高' }
const statusNames = { open: '待处理', in_progress: '处理中', resolved: '已解决' }

type Draft = Pick<Issue, 'title' | 'description' | 'priority'>

function Modal({ label, onClose, children, className = '' }: { label: string; onClose: () => void; children: ReactNode; className?: string }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const element = dialog.current!
    element.showModal()
    return () => { element.close(); previous?.focus() }
  }, [])
  return <dialog ref={dialog} className={className} aria-label={label} onCancel={event => { event.preventDefault(); onClose() }} onKeyDown={event => {
    if (event.key !== 'Tab') return
    const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]'))
    const first = controls[0], last = controls[controls.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
    if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
  }}>{children}</dialog>
}

function IssueCard({ issue, address, draft, setDraft, clearSavedDraft, error, message, setError, setMessage, saving, beginOperation, endOperation, onSaved, onDeleted }: { issue: Issue; address: string; draft: Draft | null; setDraft: (draft: Draft | null) => void; clearSavedDraft: (saved: Draft) => void; error: string; message: string; setError: (value: string) => void; setMessage: (value: string) => void; saving: boolean; beginOperation: () => boolean; endOperation: () => void; onSaved: () => void; onDeleted: () => void }) {
  const [expanded, setExpanded] = useState(false)
  const [failedPhoto, setFailedPhoto] = useState('')
  const [imageRetry, setImageRetry] = useState(0)
  const [showModel, setShowModel] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [confirmedTitle, setConfirmedTitle] = useState('')
  const photo = `${serverAddress(address)}/api/issues/${encodeURIComponent(issue.id)}/snapshot?v=${encodeURIComponent(issue.updatedAt)}&retry=${imageRetry}`
  const imageError = failedPhoto === photo

  async function saveContent(event: React.FormEvent) {
    event.preventDefault()
    if (!draft) return
    if (!draft.title.trim()) { setError('请填写问题标题'); return }
    if (!beginOperation()) return
    setError(''); setMessage('')
    try {
      await updateContent(address, issue.id, { ...draft, title: draft.title.trim() })
      clearSavedDraft(draft); onSaved(); setMessage('内容已保存')
    } catch (reason) { setError(`保存失败：${errorMessage(reason)}。草稿已保留，可重试。`) }
    finally { endOperation() }
  }

  async function changeStatus(status: Issue['status']) {
    if (status === issue.status || !beginOperation()) return
    setError('')
    setMessage('')
    try {
      await updateStatus(address, issue.id, status)
      onSaved()
      setMessage('状态已保存')
    } catch (reason) {
      setError(`保存失败：${errorMessage(reason)}。可重试或刷新确认。`)
    } finally { endOperation() }
  }

  async function permanentlyDelete() {
    if (confirmedTitle !== issue.title) return
    if (!beginOperation()) return
    setError(''); setMessage('')
    try { await deleteIssue(address, issue.id, confirmedTitle); onDeleted() }
    catch (reason) { setError(`${errorMessage(reason)}。记录和输入已保留；结果待确认时可刷新核对。`) }
    finally { endOperation() }
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
      {expanded && <Modal label="现场大图" onClose={() => setExpanded(false)}><button autoFocus onClick={() => setExpanded(false)}>关闭大图</button><img src={photo} alt={`${issue.title} 现场大图`} onError={() => setFailedPhoto(photo)} />{imageError && <p role="alert">图片加载失败，请关闭后重试。</p>}</Modal>}
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
    {confirmDelete && <Modal className="delete-confirmation" label="永久删除问题" onClose={() => { if (!saving) setConfirmDelete(false) }}>
      <h3>永久删除“{issue.title}”</h3>
      <p>问题、现场照片及扫描模型将永久删除，无法恢复。请输入当前完整标题确认。</p>
      <label>确认标题<input autoFocus aria-label="确认删除标题" value={confirmedTitle} disabled={saving} onChange={event => setConfirmedTitle(event.target.value)} /></label>
      <button disabled={saving || confirmedTitle !== issue.title} onClick={() => { void permanentlyDelete() }}>确认永久删除</button>
      <button disabled={saving} onClick={() => setConfirmDelete(false)}>取消删除</button>
      {saving && <p role="status">正在保存，请等待…</p>}
      {error && <p role="alert">{error}</p>}
    </Modal>}
    {saving && <p role="status">正在保存…</p>}
    {message && <p role="status">{message}</p>}
    {error && !confirmDelete && <p role="alert">{error}</p>}
  </article>
}

export default function App() {
  const [address, setAddress] = useState(() => import.meta.env.VITE_ARWORK_API || window.location.origin)
  const [activeAddress, setActiveAddress] = useState(address)
  const receiverInView = useRef(activeAddress)
  useEffect(() => { receiverInView.current = activeAddress }, [activeAddress])
  const [revision, setRevision] = useState(0)
  const [issues, setIssues] = useState<Issue[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [connection, setConnection] = useState('正在连接实时通知…')
  const [selected, setSelected] = useState<string | null>(null)
  const [drafts, setDrafts] = useState<Record<string, Draft>>({})
  const [pending, setPending] = useState<Record<string, boolean>>({})
  const [feedback, setFeedback] = useState<Record<string, { error: string; message: string }>>({})
  const operations = useRef(new Set<string>())
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<Issue['status'] | ''>('')
  const [priority, setPriority] = useState<Issue['priority'] | ''>('')
  const [sort, setSort] = useState('created')
  const [notice, setNotice] = useState('')
  const opener = useRef<HTMLElement | null>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const query = search.trim().toLocaleLowerCase()
  const visible = issues.filter(issue => (!status || issue.status === status) && (!priority || issue.priority === priority) && (!query || `${issue.title} ${issue.description}`.toLocaleLowerCase().includes(query))).sort((a, b) => {
    const rank = { high: 3, medium: 2, low: 1 }
    const difference = sort === 'priority' ? rank[b.priority] - rank[a.priority] : Date.parse(sort === 'updated' ? b.updatedAt : b.createdAt) - Date.parse(sort === 'updated' ? a.updatedAt : a.createdAt)
    return difference || a.id.localeCompare(b.id)
  })
  const current = issues.find(issue => issue.id === selected)
  const currentKey = current ? `${activeAddress}/${current.id}` : ''
  function updateFeedback(key: string, field: 'error' | 'message', value: string) {
    setFeedback(previous => ({ ...previous, [key]: { error: previous[key]?.error || '', message: previous[key]?.message || '', [field]: value } }))
  }
  function restoreFocus() {
    if (opener.current?.isConnected) opener.current.focus()
    else document.querySelector<HTMLInputElement>('input[type="search"]')?.focus()
  }
  function closeDetails() { setSelected(null); restoreFocus() }
  useEffect(() => { if (selected) closeButton.current?.focus() }, [selected])
  const selectedInView = useRef(selected)
  useEffect(() => { selectedInView.current = selected }, [selected])
  function forgetDraft(id: string) {
    setDrafts(previous => { const next = { ...previous }; delete next[`${activeAddress}/${id}`]; return next })
  }


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
      .then(data => { if (active) { setIssues(data); setError('')
        if (selectedInView.current && !data.some(issue => issue.id === selectedInView.current)) {
          setSelected(null); setNotice('当前问题已被删除，详情及草稿已清除'); restoreFocus()
        }
        setDrafts(previous => Object.fromEntries(Object.entries(previous).filter(([key]) => !key.startsWith(`${activeAddress}/`) || data.some(issue => key === `${activeAddress}/${issue.id}`))))
 } })
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
        if (normalized !== activeAddress) { setSelected(null); setIssues([]); setConnection('正在连接实时通知…') }
        setActiveAddress(normalized)
        setRevision(value => value + 1)
      } catch (reason) { setError(reason instanceof Error ? reason.message : '服务地址无效') }
    }}>
      <label htmlFor="server">后端地址</label>
      <input id="server" value={address} onChange={event => setAddress(event.target.value)} required />
      <button disabled={loading && address === activeAddress}>{loading && address === activeAddress ? '加载中…' : '连接并刷新'}</button>
    </form>
    <p className="receiver-status" role="status">接收端：{activeAddress} · {connection}</p>
    {error && <section role="alert"><strong>{error}</strong><p>确认 Go 服务已启动、地址正确且允许浏览器跨域访问。</p><button onClick={() => { setLoading(true); setError(''); setRevision(value => value + 1) }} disabled={loading}>重试</button></section>}
    {loading && <p role="status">正在加载问题…</p>}
    {notice && <p role="status">{notice}</p>}
    <section className="statistics" aria-label="问题统计">
      {(['', 'open', 'in_progress', 'resolved'] as const).map(value => <button key={value} aria-pressed={status === value} onClick={() => setStatus(value)}><span>{value ? statusNames[value] : '全部'}</span><strong>{value ? issues.filter(issue => issue.status === value).length : issues.length}</strong></button>)}
    </section>
    <section className="toolbar" aria-label="搜索与筛选">
      <label className="search">搜索标题或描述<input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="输入关键词" /></label>
      <label>处理状态<select value={status} onChange={event => setStatus(event.target.value as typeof status)}><option value="">全部状态</option>{Object.entries(statusNames).map(([value, name]) => <option key={value} value={value}>{name}</option>)}</select></label>
      <label>优先级筛选<select value={priority} onChange={event => setPriority(event.target.value as typeof priority)}><option value="">全部优先级</option>{Object.entries(priorityNames).map(([value, name]) => <option key={value} value={value}>{name}</option>)}</select></label>
      <label>排序<select value={sort} onChange={event => setSort(event.target.value)}><option value="created">创建时间 · 最新优先</option><option value="updated">更新时间 · 最新优先</option><option value="priority">优先级 · 高到低</option></select></label>
      <button onClick={() => { setSearch(''); setStatus(''); setPriority('') }}>清除筛选</button>
    </section>
    <p role="status">显示 {visible.length} / {issues.length} 条问题{Object.keys(drafts).filter(key => key.startsWith(`${activeAddress}/`)).length > 0 && ' · 有未保存草稿（切换记录可继续编辑）'}</p>
    {!loading && !error && issues.length === 0 && <p className="empty">暂无问题。请先在手机上选点并提交，再点击刷新。</p>}
    {!loading && !error && issues.length > 0 && visible.length === 0 && <p className="empty">没有匹配的问题。请修改关键词或清除筛选。</p>}
    <div className={`workspace ${current ? 'with-details' : ''}`}>
      <section aria-label="问题列表" className="table-wrap">
        <table><thead><tr><th>现场照片</th><th>问题</th><th>优先级</th><th>处理状态</th><th>创建时间</th><th>更新时间</th></tr></thead>
          <tbody>{visible.map(issue => <tr key={issue.id} aria-selected={selected === issue.id}>
            <td><Thumbnail issue={issue} address={activeAddress} /></td>
            <td><button className="issue-link" aria-label={`查看 ${issue.title}`} onClick={event => { opener.current = event.currentTarget; setSelected(issue.id); setNotice('') }}>{issue.title}</button><p className="summary">{issue.description || '未填写描述'}</p>{drafts[`${activeAddress}/${issue.id}`] && <small>未保存草稿</small>}</td>
            <td><span className={`priority ${issue.priority}`}>{priorityNames[issue.priority]}</span></td><td>{statusNames[issue.status]}</td><td>{new Date(issue.createdAt).toLocaleString()}</td><td>{new Date(issue.updatedAt).toLocaleString()}</td>
          </tr>)}</tbody>
        </table>
      </section>
      {current && <aside className="details" aria-label="问题详情" onKeyDown={event => { if (event.key === 'Escape' && !(event.target as HTMLElement).closest('dialog')) { event.stopPropagation(); closeDetails() } }}>
        <div className="detail-heading"><h2>问题详情</h2><button ref={closeButton} onClick={closeDetails}>关闭详情</button></div>
        {!visible.some(issue => issue.id === current.id) && <p className="hint">当前问题不符合筛选，详情和草稿仍保留。</p>}
        <IssueCard key={currentKey} issue={current} address={activeAddress} draft={drafts[currentKey] || null} error={feedback[currentKey]?.error || ''} message={feedback[currentKey]?.message || ''} setError={value => updateFeedback(currentKey, 'error', value)} setMessage={value => updateFeedback(currentKey, 'message', value)} saving={!!pending[currentKey]} beginOperation={() => {
          const key = currentKey
          if (operations.current.has(key)) return false
          operations.current.add(key); setPending(previous => ({ ...previous, [key]: true })); return true
        }} endOperation={() => {
          const key = currentKey
          operations.current.delete(key); setPending(previous => { const next = { ...previous }; delete next[key]; return next })
        }} clearSavedDraft={saved => setDrafts(previous => {
          const key = currentKey
          if (previous[key] !== saved) return previous
          const next = { ...previous }; delete next[key]; return next
        })} setDraft={draft => { if (draft) setDrafts(previous => ({ ...previous, [currentKey]: draft })); else forgetDraft(current.id) }} onSaved={() => setRevision(value => value + 1)} onDeleted={() => { forgetDraft(current.id); if (receiverInView.current !== activeAddress) return; setIssues(previous => previous.filter(issue => issue.id !== current.id)); setSelected(previous => previous === current.id ? null : previous); setNotice('问题及关联附件已永久删除'); restoreFocus(); setRevision(value => value + 1) }} />
      </aside>}
    </div>
  </main>
}

function Thumbnail({ issue, address }: { issue: Issue; address: string }) {
  const [failed, setFailed] = useState('')
  const [retry, setRetry] = useState(0)
  const photo = `${serverAddress(address)}/api/issues/${encodeURIComponent(issue.id)}/snapshot?v=${encodeURIComponent(issue.updatedAt)}&retry=${retry}`
  if (!issue.snapshotUrl) return <span className="no-photo">无快照</span>
  return failed === photo ? <button className="thumbnail-retry" aria-label={`${issue.title} 重试缩略图`} onClick={() => setRetry(value => value + 1)}>重试图片</button> : <img className="thumbnail" loading="lazy" src={photo} alt={`${issue.title} 缩略图`} onError={() => setFailed(photo)} />
}
