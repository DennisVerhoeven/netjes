import { definePage } from 'netjes';
import { Greeting } from '../components/Greeting.tsx';
import { HelloForm } from '../components/HelloForm.tsx';
import { pages } from '../generated/netjes.ts';

// The props schema is generated from the C# HomeProps record (Features/Home/HomePage.cs).
export default definePage(pages['/'], function Home({ message, renderedAt }) {
  return (
    <div className="space-y-12">
      <Greeting message={message} renderedAt={renderedAt} />
      <HelloForm />
    </div>
  );
});
